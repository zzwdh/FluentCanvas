use crate::Status;
use std::{borrow::Cow, sync::Arc};

// A standard brush edit replaces only the versioned render prefix. Geometry,
// pen packet channels and optional template bytes are shared by all versions.
#[derive(Clone)]
pub(crate) struct StoredRecord {
    base: Arc<[u8]>,
    brush: Option<Arc<[u8]>>,
    version: u64,
    local_bounds: [f64; 4],
    placement: Option<Arc<[f64; 6]>>,
}

impl StoredRecord {
    pub(crate) fn from_base(base: Arc<[u8]>, version: u64) -> Self {
        let count = u32::from_le_bytes(base[8..12].try_into().unwrap()) as usize;
        let mut bounds = [
            f64::INFINITY,
            f64::INFINITY,
            f64::NEG_INFINITY,
            f64::NEG_INFINITY,
        ];
        for p in base[16..16 + 20 * count].chunks_exact(20) {
            let x = f64::from_le_bytes(p[..8].try_into().unwrap());
            let y = f64::from_le_bytes(p[8..16].try_into().unwrap());
            bounds = [
                bounds[0].min(x),
                bounds[1].min(y),
                bounds[2].max(x),
                bounds[3].max(y),
            ];
        }
        Self {
            base,
            brush: None,
            version,
            local_bounds: bounds,
            placement: None,
        }
    }
    pub(crate) fn version(&self) -> u64 {
        self.version
    }
    pub(crate) fn shares_geometry(&self, other: &Self) -> bool {
        Arc::ptr_eq(&self.base, &other.base) && self.placement == other.placement
    }
    fn render_start(&self) -> usize {
        self.base.len() - u32::from_le_bytes(self.base[12..16].try_into().unwrap()) as usize
    }
    fn base_brush(&self) -> Result<&[u8], Status> {
        let render = &self.base[self.render_start()..];
        if render.first() != Some(&1) {
            return Err(Status::Invalid);
        }
        let n = match render.get(25) {
            Some(0) => 26,
            Some(1) => 74,
            _ => return Err(Status::Invalid),
        };
        render.get(..n).ok_or(Status::Invalid)
    }
    pub(crate) fn brush(&self) -> Result<&[u8], Status> {
        match &self.brush {
            Some(value) => Ok(value),
            None => self.base_brush(),
        }
    }
    pub(crate) fn with_brush(&self, brush: &[u8], version: u64) -> Result<Self, Status> {
        self.base_brush()?;
        if brush.first() != Some(&1)
            || !matches!((brush.get(25), brush.len()), (Some(0), 26) | (Some(1), 74))
        {
            return Err(Status::Invalid);
        }
        Ok(Self {
            brush: Some(brush.into()),
            version,
            ..self.clone()
        })
    }

    pub(crate) fn placed(
        &self,
        delta: [f64; 6],
        min: f64,
        max: f64,
        width: f64,
        height: f64,
        version: u64,
    ) -> Option<Self> {
        let matrix = if let Some(m) = &self.placement {
            let [a, b, c, d, x, y] = **m;
            let [e, f, g, h, tx, ty] = delta;
            [
                a * e + b * g,
                a * f + b * h,
                c * e + d * g,
                c * f + d * h,
                x * e + y * g + tx,
                x * f + y * h + ty,
            ]
        } else {
            delta
        };
        let [left, top, right, bottom] = self.local_bounds;
        for (x, y) in [(left, top), (right, top), (left, bottom), (right, bottom)] {
            let (x, y) = mapped(&matrix, x, y);
            if !(min..=max).contains(&x) || !(min..=max).contains(&y) {
                return None;
            }
        }
        let mut brush = self.brush().ok()?.to_vec();
        brush[5..13].copy_from_slice(&width.to_le_bytes());
        brush[13..21].copy_from_slice(&height.to_le_bytes());
        let mut record = self.with_brush(&brush, version).ok()?;
        record.placement = Some(Arc::new(matrix));
        Some(record)
    }

    pub(crate) fn has_shape(&self) -> bool {
        self.base_brush()
            .ok()
            .and_then(|brush| self.base.get(self.render_start() + brush.len()))
            .is_some_and(|v| v & 4 != 0)
    }

    pub(crate) fn world_bounds(&self) -> [f64; 4] {
        let [l, t, r, b] = self.local_bounds;
        let Some(m) = self.placement.as_ref() else {
            return self.local_bounds;
        };
        let (x, y) = mapped(m, l, t);
        let mut bounds = [x, y, x, y];
        for (x, y) in [(l, b), (r, t), (r, b)] {
            let (x, y) = mapped(m, x, y);
            bounds = [
                bounds[0].min(x),
                bounds[1].min(y),
                bounds[2].max(x),
                bounds[3].max(y),
            ];
        }
        bounds
    }
    pub(crate) fn len(&self) -> usize {
        match &self.brush {
            Some(brush) => self.base.len() - self.base_brush().unwrap().len() + brush.len(),
            None => self.base.len(),
        }
    }
    pub(crate) fn copy_range(&self, offset: usize, out: &mut [u8]) -> usize {
        if out.is_empty() || offset >= self.len() {
            return 0;
        }
        let Some(brush) = &self.brush else {
            let count = out.len().min(self.base.len() - offset);
            out[..count].copy_from_slice(&self.base[offset..offset + count]);
            return count;
        };
        let start = self.render_start();
        let size = (self.len() - start) as u32;
        let length_bytes = size.to_le_bytes();
        let original = self.base_brush().unwrap().len();
        let chunks = [
            &self.base[..12],
            length_bytes.as_ref(),
            &self.base[16..start],
            brush.as_ref(),
            &self.base[start + original..],
        ];
        let mut skip = offset;
        let mut written = 0;
        for part in chunks {
            if skip >= part.len() {
                skip -= part.len();
                continue;
            }
            let count = (part.len() - skip).min(out.len() - written);
            out[written..written + count].copy_from_slice(&part[skip..skip + count]);
            written += count;
            if written == out.len() {
                break;
            }
            skip = 0;
        }
        if let Some(matrix) = &self.placement {
            let begin = offset.max(16);
            let end = (offset + written).min(start);
            if begin < end {
                for i in (begin - 16) / 20..=(end - 17) / 20 {
                    let pos = 16 + i * 20;
                    let point = &self.base[pos..pos + 16];
                    let (x, y) = mapped(
                        matrix,
                        f64::from_le_bytes(point[..8].try_into().unwrap()),
                        f64::from_le_bytes(point[8..].try_into().unwrap()),
                    );
                    for (at, bytes) in [(pos, x.to_le_bytes()), (pos + 8, y.to_le_bytes())] {
                        let from = at.max(begin);
                        let to = (at + 8).min(end);
                        if from < to {
                            out[from - offset..to - offset]
                                .copy_from_slice(&bytes[from - at..to - at]);
                        }
                    }
                }
            }
        }
        written
    }
    pub(crate) fn bytes(&self) -> Cow<'_, [u8]> {
        if self.brush.is_none() {
            return Cow::Borrowed(&self.base);
        }
        let mut result = vec![0; self.len()];
        self.copy_range(0, &mut result);
        Cow::Owned(result)
    }
}

fn mapped(m: &[f64; 6], x: f64, y: f64) -> (f64, f64) {
    (x * m[0] + y * m[2] + m[4], x * m[1] + y * m[3] + m[5])
}
