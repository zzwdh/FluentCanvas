use std::collections::{HashMap, HashSet};
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::Arc;

use crate::Status;

pub(crate) use crate::record::StoredRecord;
use std::borrow::Cow;
type Payload = StoredRecord;
type StrokeId = u64;
static NEXT_TOKEN: AtomicU64 = AtomicU64::new(1);

pub(crate) fn reserve_tokens(count: usize) -> Result<u64, Status> {
    NEXT_TOKEN
        .fetch_update(Ordering::Relaxed, Ordering::Relaxed, |next| {
            next.checked_add(count as u64)
        })
        .map_err(|_| Status::Invalid)
}

// The packed format deliberately does not depend on Rust/.NET struct layout.
// Each entry consists of [id:u64, points:u32, render_bytes:u32], followed by
// points * [x:f64, y:f64, pressure:f32] and an opaque host render payload.
// All fields are little endian. The host payload carries drawing attributes
// and any extra stylus channels beyond the validated native geometry.
fn parse_records(bytes: &[u8]) -> Result<Vec<(StrokeId, Payload)>, Status> {
    parse_numbered_records(bytes, None)
}
fn parse_numbered_records(
    mut bytes: &[u8],
    first: Option<u64>,
) -> Result<Vec<(StrokeId, Payload)>, Status> {
    let mut records = Vec::new();
    let mut seen = HashSet::new();
    while !bytes.is_empty() {
        let header = bytes.get(..16).ok_or(Status::Invalid)?;
        let id = u64::from_le_bytes(header[..8].try_into().unwrap());
        let count = u32::from_le_bytes(header[8..12].try_into().unwrap()) as usize;
        let render_size = u32::from_le_bytes(header[12..].try_into().unwrap()) as usize;
        if id == 0 || !seen.insert(id) || count == 0 || render_size == 0 {
            return Err(Status::Invalid);
        }
        let sample_end = count
            .checked_mul(20)
            .and_then(|n| n.checked_add(16))
            .ok_or(Status::Invalid)?;
        let record_size = sample_end.checked_add(render_size).ok_or(Status::Invalid)?;
        let record = bytes.get(..record_size).ok_or(Status::Invalid)?;
        for point in record[16..sample_end].chunks_exact(20) {
            let x = f64::from_le_bytes(point[..8].try_into().unwrap());
            let y = f64::from_le_bytes(point[8..16].try_into().unwrap());
            let pressure = f32::from_le_bytes(point[16..].try_into().unwrap());
            if !x.is_finite() || !y.is_finite() || !(0.0..=1.0).contains(&pressure) {
                return Err(Status::Invalid);
            }
        }
        let mut owned: Arc<[u8]> = Arc::from(record);
        let runtime_id = match first {
            Some(base) => {
                let assigned = base
                    .checked_add(records.len() as u64)
                    .ok_or(Status::Invalid)?;
                Arc::get_mut(&mut owned).unwrap()[..8].copy_from_slice(&assigned.to_le_bytes());
                assigned
            }
            None => id,
        };
        records.push((
            runtime_id,
            StoredRecord::from_base(owned, reserve_tokens(1)?),
        ));
        bytes = &bytes[record_size..];
    }
    Ok(records)
}

#[derive(Clone)]
struct Replacement {
    id: StrokeId,
    before: Option<Payload>,
    after: Option<Payload>,
}

#[derive(Clone)]
struct Splice {
    start: usize,
    removed: Vec<StrokeId>,
    inserted: Vec<StrokeId>,
}

#[derive(Clone)]
struct Edit {
    media: Vec<crate::scene::Change>,
    splices: Vec<Splice>,
    replacements: Vec<Replacement>,
    token: u64,
}

#[derive(Clone)]
pub struct Core {
    pub(crate) spatial: crate::hit::SpatialCache,
    pub(crate) scene: crate::scene::Scene,
    order: Vec<StrokeId>,
    labels: HashMap<StrokeId, u128>,
    strokes: HashMap<StrokeId, Payload>,
    journal: Vec<Arc<Edit>>,
    cursor: usize,
    origin: u64,
    saved: u64,
}

impl Core {
    pub(crate) fn new() -> Result<Self, Status> {
        let token = reserve_tokens(1)?;
        Ok(Self {
            spatial: Default::default(),
            scene: Default::default(),
            order: Vec::new(),
            labels: HashMap::new(),
            strokes: HashMap::new(),
            journal: Vec::new(),
            cursor: 0,
            origin: token,
            saved: token,
        })
    }
    pub(crate) fn state(&self) -> (usize, usize, u64, u64) {
        let current = self
            .cursor
            .checked_sub(1)
            .map_or(self.origin, |i| self.journal[i].token);
        (self.cursor, self.journal.len(), current, self.saved)
    }

    pub(crate) fn mark_saved(&mut self) {
        self.saved = self.state().2;
    }

    pub(crate) fn order(&self) -> &[StrokeId] {
        &self.order
    }

    pub(crate) fn record(&self, id: u64) -> Result<&StoredRecord, Status> {
        self.strokes.get(&id).ok_or(Status::Missing)
    }
    pub(crate) fn read(&self, id: u64) -> Result<Cow<'_, [u8]>, Status> {
        Ok(self.record(id)?.bytes())
    }
    pub(crate) fn freeze(&self) -> crate::frozen::FrozenPage {
        crate::frozen::FrozenPage::new(self.order.iter().map(|id| self.strokes[id].clone()))
    }
    // Common pen-up operation has cost proportional only to the appended strokes.
    pub(crate) fn append(&mut self, encoded: &[u8]) -> Result<(), Status> {
        let records = parse_records(encoded)?;
        let mut ids = Vec::with_capacity(records.len());
        let mut replacements = Vec::with_capacity(records.len());
        for (id, payload) in records {
            if self.strokes.contains_key(&id) {
                return Err(Status::Invalid);
            }
            ids.push(id);
            replacements.push(Replacement {
                id,
                before: None,
                after: Some(payload),
            });
        }
        self.commit(Edit {
            media: Vec::new(),
            splices: vec![Splice {
                start: self.order.len(),
                removed: Vec::new(),
                inserted: ids,
            }],
            replacements,
            token: reserve_tokens(1)?,
        });
        Ok(())
    }

    // Pressure simulation can finish after pen-up. Rewrite the life span of
    // that version between the closest two edits touching the same stroke.
    // Independent page snapshots retain their original immutable payloads.
    pub(crate) fn finish_stroke(
        &mut self,
        id: StrokeId,
        style: i32,
        elapsed_ms: f64,
    ) -> Result<bool, Status> {
        let Some(record) = crate::brush::finish(&self.read(id)?, style, elapsed_ms) else {
            return Ok(false);
        };
        let payload = StoredRecord::from_base(Arc::from(record), reserve_tokens(1)?);
        let prior = self.journal[..self.cursor].iter().rposition(|edit| {
            edit.replacements
                .iter()
                .any(|replacement| replacement.id == id)
        });
        let next = self.journal[self.cursor..]
            .iter()
            .position(|edit| {
                edit.replacements
                    .iter()
                    .any(|replacement| replacement.id == id)
            })
            .map(|offset| self.cursor + offset);
        let start = prior.map_or(0, |i| i + 1);
        let last = next.unwrap_or(self.journal.len());
        let first_token = reserve_tokens(last - start + 1)?;
        if let Some(i) = prior {
            let edit = Arc::make_mut(&mut self.journal[i]);
            let replacement = edit.replacements.iter_mut().find(|r| r.id == id).unwrap();
            replacement.after = Some(payload.clone());
        }
        if let Some(i) = next {
            let edit = Arc::make_mut(&mut self.journal[i]);
            let replacement = edit.replacements.iter_mut().find(|r| r.id == id).unwrap();
            replacement.before = Some(payload.clone());
        }
        self.strokes.insert(id, payload);
        self.spatial.invalidate(id);
        for index in start..=last {
            let token = first_token + (index - start) as u64;
            if index == 0 {
                self.origin = token;
            } else {
                Arc::make_mut(&mut self.journal[index - 1]).token = token;
            }
        }
        Ok(true)
    }

    pub(crate) fn apply(
        &mut self,
        order: &[StrokeId],
        encoded: &[u8],
        reset: bool,
    ) -> Result<(), Status> {
        let updates: HashMap<_, _> = parse_records(encoded)?.into_iter().collect();
        let mut retained = HashSet::with_capacity(order.len());
        for id in order {
            if *id == 0
                || !retained.insert(*id)
                || !(updates.contains_key(id) || self.strokes.contains_key(id))
            {
                return Err(Status::Invalid);
            }
        }
        if !updates.keys().all(|id| retained.contains(id)) {
            return Err(Status::Invalid);
        }
        if reset {
            let token = reserve_tokens(1)?;
            self.spatial = Default::default();
            self.strokes.retain(|id, _| retained.contains(id));
            self.strokes.extend(updates);
            self.order = order.to_vec();
            self.relabel();
            self.journal.clear();
            self.cursor = 0;
            self.origin = token;
            self.saved = token;
            return Ok(());
        }

        let start = self
            .order
            .iter()
            .zip(order)
            .take_while(|(a, b)| a == b)
            .count();
        let tail = self.order[start..]
            .iter()
            .rev()
            .zip(order[start..].iter().rev())
            .take_while(|(a, b)| a == b)
            .count();
        let mut replacements: Vec<_> = self
            .strokes
            .iter()
            .filter(|(id, _)| !retained.contains(*id))
            .map(|(&id, data)| Replacement {
                id,
                before: Some(data.clone()),
                after: None,
            })
            .collect();
        for (id, data) in updates {
            replacements.push(Replacement {
                id,
                before: self.strokes.get(&id).cloned(),
                after: Some(data),
            });
        }
        self.commit(Edit {
            media: Vec::new(),
            splices: vec![Splice {
                start,
                removed: self.order[start..self.order.len() - tail].to_vec(),
                inserted: order[start..order.len() - tail].to_vec(),
            }],
            replacements,
            token: reserve_tokens(1)?,
        });
        Ok(())
    }

    fn commit(&mut self, mut edit: Edit) {
        edit.media = std::mem::take(&mut self.scene.pending);
        self.install(&edit, true);
        self.journal.truncate(self.cursor);
        self.journal.push(Arc::new(edit));
        self.cursor += 1;
    }

    fn install(&mut self, edit: &Edit, forward: bool) {
        self.scene.install(&edit.media, forward);
        if forward {
            for change in &edit.splices {
                self.splice(change.start, &change.removed, &change.inserted);
            }
        } else {
            for change in edit.splices.iter().rev() {
                self.splice(change.start, &change.inserted, &change.removed);
            }
        }
        for replacement in &edit.replacements {
            self.spatial.invalidate(replacement.id);
            let version = if forward {
                &replacement.after
            } else {
                &replacement.before
            };
            match version {
                Some(record) => {
                    self.strokes.insert(replacement.id, record.clone());
                }
                None => {
                    self.strokes.remove(&replacement.id);
                }
            }
        }
    }

    pub(crate) fn step(&mut self, forward: bool) -> Result<(), Status> {
        let index = if forward {
            self.cursor
        } else {
            self.cursor.checked_sub(1).ok_or(Status::Missing)?
        };
        let edit = self.journal.get(index).cloned().ok_or(Status::Missing)?;
        self.install(&edit, forward);
        self.cursor = if forward { index + 1 } else { index };
        Ok(())
    }
}

impl Core {
    // Order labels make locating an erased ID logarithmic without updating
    // every shifted ID. Vec::splice still moves the packed suffix in memory.
    fn relabel(&mut self) {
        self.labels.clear();
        for (index, id) in self.order.iter().enumerate() {
            self.labels.insert(*id, ((index as u128) + 1) << 64);
        }
    }
    pub(crate) fn position(&self, id: u64) -> Option<usize> {
        let key = self.labels.get(&id)?;
        self.order
            .binary_search_by_key(key, |id| self.labels[id])
            .ok()
    }
    fn splice(&mut self, start: usize, remove: &[u64], add: &[u64]) {
        if remove.is_empty() && add.is_empty() {
            return;
        }
        for id in remove {
            self.labels.remove(id);
        }
        let lower = if start == 0 {
            0
        } else {
            self.labels[&self.order[start - 1]]
        };
        let after = start + remove.len();
        let upper = self
            .order
            .get(after)
            .map(|id| self.labels[id])
            .unwrap_or(u128::MAX);
        let spacing = ((upper - lower) / (add.len() as u128 + 1)).min(1u128 << 64);
        self.order.splice(start..after, add.iter().copied());
        if spacing == 0 && !add.is_empty() {
            self.relabel();
            return;
        }
        for (i, id) in add.iter().enumerate() {
            self.labels.insert(*id, lower + (i as u128 + 1) * spacing);
        }
    }

    pub(crate) fn load_document(
        &mut self,
        bytes: &[u8],
        first: u64,
        undoable: bool,
    ) -> Result<(), Status> {
        if first == 0 {
            return Err(Status::Invalid);
        }
        let records = parse_numbered_records(bytes, Some(first))?;
        let mut baseline = Self::new()?;
        baseline.scene.id = self.scene.id;
        let mut replacements = Vec::with_capacity(records.len());
        let mut ids = Vec::with_capacity(records.len());
        for (id, data) in records {
            ids.push(id);
            replacements.push(Replacement {
                id,
                before: None,
                after: Some(data),
            });
        }
        if !ids.is_empty() {
            baseline.commit(Edit {
                media: Vec::new(),
                splices: vec![Splice {
                    start: 0,
                    removed: vec![],
                    inserted: ids,
                }],
                replacements,
                token: reserve_tokens(1)?,
            });
            if !undoable {
                baseline.journal.clear();
                baseline.cursor = 0;
            }
        }
        baseline.mark_saved();
        *self = baseline;
        Ok(())
    }

    pub(crate) fn commit_records(
        &mut self,
        records: Vec<(u64, StoredRecord)>,
    ) -> Result<(), Status> {
        let mut replacements = Vec::with_capacity(records.len());
        let mut seen = HashSet::new();
        for (id, data) in records {
            if !seen.insert(id) {
                return Err(Status::Invalid);
            }
            let before = self.record(id)?.clone();
            replacements.push(Replacement {
                id,
                before: Some(before),
                after: Some(data),
            });
        }
        self.commit(Edit {
            media: Vec::new(),
            splices: vec![],
            replacements,
            token: reserve_tokens(1)?,
        });
        Ok(())
    }
    pub(crate) fn update(&mut self, bytes: &[u8], mut brushes: &[u8]) -> Result<(), Status> {
        let mut records = parse_records(bytes)?;
        while !brushes.is_empty() {
            let head = brushes.get(..12).ok_or(Status::Invalid)?;
            let id = u64::from_le_bytes(head[..8].try_into().unwrap());
            let size = u32::from_le_bytes(head[8..].try_into().unwrap()) as usize;
            let brush = brushes.get(12..12 + size).ok_or(Status::Invalid)?;
            records.push((id, self.record(id)?.with_brush(brush, reserve_tokens(1)?)?));
            brushes = &brushes[12 + size..];
        }
        self.commit_records(records)
    }
    pub(crate) fn layers(
        &mut self,
        remove: &[u64],
        positions: &[u64],
        data: &[u8],
    ) -> Result<(), Status> {
        let records = parse_records(data)?;
        if positions.len() != records.len() {
            return Err(Status::Invalid);
        }
        let mut splices = Vec::new();
        let mut removals = Vec::with_capacity(remove.len());
        let mut replacements = Vec::new();
        let mut seen = HashSet::new();
        for &id in remove {
            if !seen.insert(id) {
                return Err(Status::Invalid);
            }
            let index = self.position(id).ok_or(Status::Missing)?;
            removals.push((index, id));
            replacements.push(Replacement {
                id,
                before: Some(self.record(id)?.clone()),
                after: None,
            });
        }
        removals.sort_unstable_by(|a, b| b.0.cmp(&a.0));
        for run in removals.chunk_by(|a, b| a.0 == b.0 + 1) {
            splices.push(Splice {
                start: run.last().unwrap().0,
                removed: run.iter().rev().map(|x| x.1).collect(),
                inserted: Vec::new(),
            });
        }
        let mut inserts = Vec::with_capacity(records.len());
        for (&pos, (id, value)) in positions.iter().zip(records) {
            if self.strokes.contains_key(&id) {
                return Err(Status::Invalid);
            }
            inserts.push((usize::try_from(pos).map_err(|_| Status::Invalid)?, id));
            replacements.push(Replacement {
                id,
                before: None,
                after: Some(value),
            });
        }
        inserts.sort_unstable();
        for (n, &(start, _)) in inserts.iter().enumerate() {
            if (n > 0 && inserts[n - 1].0 == start) || start > self.order.len() - remove.len() + n {
                return Err(Status::Invalid);
            }
        }
        for run in inserts.chunk_by(|a, b| b.0 == a.0 + 1) {
            splices.push(Splice {
                start: run[0].0,
                removed: Vec::new(),
                inserted: run.iter().map(|x| x.1).collect(),
            });
        }
        self.commit(Edit {
            media: Vec::new(),
            splices,
            replacements,
            token: reserve_tokens(1)?,
        });
        Ok(())
    }

    // After step(), emit just the positional edits in application order.
    // Each triple is [operation, zero-based layer index, stroke ID].
    pub(crate) fn delta(&self, forward: bool) -> Result<Vec<u64>, Status> {
        let index = if forward {
            self.cursor.checked_sub(1).ok_or(Status::Missing)?
        } else {
            self.cursor
        };
        let edit = self.journal.get(index).ok_or(Status::Missing)?;
        let mut out = Vec::new();
        let mut write = |s: &Splice| {
            let (from, to) = if forward {
                (&s.removed, &s.inserted)
            } else {
                (&s.inserted, &s.removed)
            };
            for (i, &id) in from.iter().enumerate().rev() {
                out.extend([1, (s.start + i) as u64, id]);
            }
            for (i, &id) in to.iter().enumerate() {
                out.extend([2, (s.start + i) as u64, id]);
            }
        };
        if forward {
            edit.splices.iter().for_each(&mut write);
        } else {
            edit.splices.iter().rev().for_each(&mut write);
        }
        for r in &edit.replacements {
            if let (Some(before), Some(after)) = (&r.before, &r.after) {
                let i = self.position(r.id).ok_or(Status::Missing)?;
                let op = if before.shares_geometry(after) { 4 } else { 3 };
                out.extend([op, i as u64, r.id]);
            }
        }
        Ok(out)
    }
}
