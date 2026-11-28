use crate::{output, status, Core, Status};
use std::collections::{HashMap, HashSet};

type Point = [f64; 2];
type Bounds = [f64; 4];
type Cell = (i32, i32);
const CELL_SIZE: f64 = 128.;
const MAX_CELLS: i64 = 256;

#[derive(Default)]
pub(crate) struct SpatialCache {
    index: Option<Grid>,
    dirty: HashSet<u64>,
}

// Page snapshots share the immutable stroke data. Rebuild this derived cache
// only when the copied page is first queried.
impl Clone for SpatialCache {
    fn clone(&self) -> Self {
        Self::default()
    }
}

impl SpatialCache {
    pub(crate) fn invalidate(&mut self, id: u64) {
        if self.index.is_some() {
            self.dirty.insert(id);
        }
    }
}

#[derive(Default)]
struct Grid {
    cells: HashMap<Cell, HashSet<u64>>,
    bounds: HashMap<u64, Bounds>,
    overflow: HashSet<u64>,
}

fn cells(b: Bounds) -> Option<impl Iterator<Item = Cell>> {
    let [x0, y0, x1, y1] = b.map(|x| (x / CELL_SIZE).floor() as i32);
    let w = i64::from(x1) - i64::from(x0) + 1;
    let h = i64::from(y1) - i64::from(y0) + 1;
    if w.saturating_mul(h) > MAX_CELLS {
        return None;
    }
    Some((x0..=x1).flat_map(move |x| (y0..=y1).map(move |y| (x, y))))
}

fn overlaps(a: Bounds, b: Bounds) -> bool {
    a[0] <= b[2] && a[2] >= b[0] && a[1] <= b[3] && a[3] >= b[1]
}

impl Grid {
    fn remove(&mut self, id: u64) {
        let Some(b) = self.bounds.remove(&id) else {
            return;
        };
        self.overflow.remove(&id);
        if let Some(keys) = cells(b) {
            for key in keys {
                if let Some(bucket) = self.cells.get_mut(&key) {
                    bucket.remove(&id);
                    if bucket.is_empty() {
                        self.cells.remove(&key);
                    }
                }
            }
        }
    }
    fn insert(&mut self, id: u64, b: Bounds) {
        self.bounds.insert(id, b);
        if let Some(keys) = cells(b) {
            for key in keys {
                self.cells.entry(key).or_default().insert(id);
            }
        } else {
            self.overflow.insert(id);
        }
    }
    fn candidates(&self, b: Bounds) -> HashSet<u64> {
        let Some(keys) = cells(b) else {
            return self
                .bounds
                .iter()
                .filter(|(_, old)| overlaps(**old, b))
                .map(|(&id, _)| id)
                .collect();
        };
        let mut found = self.overflow.clone();
        for key in keys {
            if let Some(bucket) = self.cells.get(&key) {
                found.extend(bucket);
            }
        }
        found.retain(|id| overlaps(self.bounds[id], b));
        found
    }
}

fn f64_at(bytes: &[u8], offset: usize) -> f64 {
    f64::from_le_bytes(bytes[offset..offset + 8].try_into().unwrap())
}
struct Geometry {
    samples: Vec<(Point, f64)>,
    bounds: Bounds,
    exact: bool,
}
fn geometry(record: &[u8]) -> Result<Geometry, Status> {
    let count = u32::from_le_bytes(record[8..12].try_into().unwrap()) as usize;
    let end = 16 + count * 20;
    let brush = &record[end..];
    if brush.len() < 27 || brush[0] != 1 {
        return Err(Status::Invalid);
    }
    let width = f64_at(brush, 5);
    let height = f64_at(brush, 13);
    // Windows Ink serializes StylusTip.Rectangle = 0, Ellipse = 1.
    let exact = brush[21] == 1 && brush[22] == 0 && brush[25] == 0 && width == height;
    let radius = width * 0.5;
    let mut bounds = [
        f64::INFINITY,
        f64::INFINITY,
        f64::NEG_INFINITY,
        f64::NEG_INFINITY,
    ];
    let mut add = |xy: Point, p: f64| {
        let r = radius * if brush[23] != 0 { 1. } else { 0.25 + 1.5 * p };
        for i in 0..2 {
            bounds[i] = bounds[i].min(xy[i] - r);
            bounds[2 + i] = bounds[2 + i].max(xy[i] + r);
        }
        (xy, r)
    };
    let samples =
        if let Some(shape) = crate::shapes::project_record(record, crate::shapes::TOLERANCE)? {
            shape.into_iter().map(|p| add(p, 0.5)).collect()
        } else {
            record[16..end]
                .chunks_exact(20)
                .map(|s| {
                    add(
                        [f64_at(s, 0), f64_at(s, 8)],
                        f32::from_le_bytes(s[16..20].try_into().unwrap()) as f64,
                    )
                })
                .collect()
        };
    // WPF's smoothed Bezier and transformed/rectangular brush outlines may
    // extend beyond the sampled polyline. Let its outline hit tester handle them.
    if !exact {
        if brush[22] != 0 || brush[25] != 0 {
            bounds = [-1e30, -1e30, 1e30, 1e30];
        } else {
            let padding = width.hypot(height) * 0.875;
            bounds = [
                bounds[0] - padding,
                bounds[1] - padding,
                bounds[2] + padding,
                bounds[3] + padding,
            ];
        }
    }
    Ok(Geometry {
        samples,
        bounds,
        exact,
    })
}

#[repr(C)]
#[derive(Clone, Copy, Debug)]
pub struct Cut {
    id: u64,
    start: f64,
    end: f64,
}

fn dot(a: Point, b: Point) -> f64 {
    a[0] * b[0] + a[1] * b[1]
}
fn sub(a: Point, b: Point) -> Point {
    [a[0] - b[0], a[1] - b[1]]
}
fn at(a: Point, d: Point, t: f64) -> Point {
    [a[0] + d[0] * t, a[1] + d[1] * t]
}

// Intersect a linearly tapered round stroke segment with the swept eraser.
// Split where the nearest feature switches between either cap and the body;
// within each interval squared distance minus squared radius is quadratic.
fn cut_segment(
    a: (Point, f64),
    b: (Point, f64),
    from: Point,
    to: Point,
    r: f64,
) -> Option<(f64, f64)> {
    let v = sub(b.0, a.0);
    let e = sub(to, from);
    let z = sub(a.0, from);
    let l = dot(e, e);
    let dr = b.1 - a.1;
    let r0 = r + a.1;
    let mut breaks = [0., 1., 1., 1.];
    if l > 0. && dot(v, e) != 0. {
        for (i, k) in [0., l].into_iter().enumerate() {
            breaks[i + 2] = ((k - dot(z, e)) / dot(v, e)).clamp(0., 1.);
        }
    }
    breaks.sort_by(f64::total_cmp);
    let mut result: Option<(f64, f64)> = None;
    let mut contact = None;
    for span in breaks.windows(2) {
        let (lo, hi) = (span[0], span[1]);
        if hi <= lo {
            continue;
        }
        let along = dot(at(z, v, (lo + hi) * 0.5), e);
        let (base, delta) = if along <= 0. || l == 0. {
            (z, v)
        } else if along >= l {
            (sub(z, e), v)
        } else {
            (at(z, e, -dot(z, e) / l), at(v, e, -dot(v, e) / l))
        };
        let (qa, qb, qc) = (
            dot(delta, delta) - dr * dr,
            2. * (dot(base, delta) - r0 * dr),
            dot(base, base) - r0 * r0,
        );
        let mut cuts = [lo, hi, hi, hi];
        if qa == 0. {
            if qb != 0. {
                cuts[2] = (-qc / qb).clamp(lo, hi);
            }
        } else {
            let disc = qb.mul_add(qb, -4. * qa * qc);
            if disc >= 0. {
                let q = -0.5 * (qb + disc.sqrt().copysign(qb));
                if q != 0. {
                    cuts[2] = (q / qa).clamp(lo, hi);
                    cuts[3] = (qc / q).clamp(lo, hi);
                } else {
                    cuts[2] = (-qb / (2. * qa)).clamp(lo, hi);
                }
            }
        }
        cuts.sort_by(f64::total_cmp);
        // The closed outlines may touch at exactly one point. A whole-stroke
        // eraser must report the hit; for a point eraser this removes no length.
        for t in cuts {
            let residual = (qa * t + qb) * t + qc;
            let tolerance = 8. * f64::EPSILON * ((qa * t * t).abs() + (qb * t).abs() + qc.abs());
            if residual.abs() <= tolerance {
                contact = Some((t, t));
            }
        }
        for pair in cuts.windows(2) {
            if pair[0] >= pair[1] {
                continue;
            }
            let t = (pair[0] + pair[1]) * 0.5;
            if (qa * t + qb) * t + qc <= 0. {
                result = Some((result.map_or(pair[0], |v| v.0), pair[1]));
            }
        }
    }
    result.or(contact)
}

impl Core {
    fn cuts(&mut self, from: Point, to: Point, r: f64) -> Result<Vec<Cut>, Status> {
        if !r.is_finite() || r <= 0. || from.into_iter().chain(to).any(|v| !v.is_finite()) {
            return Err(Status::Invalid);
        }
        let mut cache = std::mem::take(&mut self.spatial);
        let index = cache.index.get_or_insert_with(|| {
            cache.dirty.extend(self.order());
            Grid::default()
        });
        for id in cache.dirty.drain() {
            index.remove(id);
            if let Ok(record) = self.record(id) {
                let brush = record.brush()?;
                let b = if record.has_shape() {
                    geometry(&record.bytes())?.bounds
                } else if brush[22] != 0 || brush[25] != 0 {
                    [-1e30, -1e30, 1e30, 1e30]
                } else {
                    let [x0, y0, x1, y1] = record.world_bounds();
                    let padding = f64_at(brush, 5).hypot(f64_at(brush, 13)) * 0.875;
                    [x0 - padding, y0 - padding, x1 + padding, y1 + padding]
                };
                index.insert(id, b);
            }
        }
        let candidates = index.candidates([
            from[0].min(to[0]) - r,
            from[1].min(to[1]) - r,
            from[0].max(to[0]) + r,
            from[1].max(to[1]) + r,
        ]);
        self.spatial = cache;
        let mut out: Vec<Cut> = Vec::new();
        for id in candidates {
            let g = geometry(&self.read(id)?)?;
            if !g.exact {
                out.push(Cut {
                    id,
                    start: -1.,
                    end: -1.,
                });
                continue;
            }
            if g.samples.len() == 1 {
                let p = g.samples[0];
                if cut_segment(p, p, from, to, r).is_some() {
                    out.push(Cut {
                        id,
                        start: 0.,
                        end: 0.,
                    });
                }
            }
            for (i, pair) in g.samples.windows(2).enumerate() {
                if let Some((a, b)) = cut_segment(pair[0], pair[1], from, to, r) {
                    let (start, end) = (i as f64 + a, i as f64 + b);
                    if let Some(last) = out.last_mut() {
                        if last.id == id && last.end >= start {
                            last.end = end;
                            continue;
                        }
                    }
                    out.push(Cut { id, start, end });
                }
            }
        }
        Ok(out)
    }
}

/// # Safety
/// core is exclusively owned; dest and required obey the common output ABI.
#[no_mangle]
pub unsafe extern "C" fn fc_core_erase_query(
    core: *mut Core,
    x0: f64,
    y0: f64,
    x1: f64,
    y1: f64,
    radius: f64,
    dest: *mut Cut,
    capacity: usize,
    required: *mut usize,
) -> i32 {
    status(|| {
        let cuts = core
            .as_mut()
            .ok_or(Status::Invalid)?
            .cuts([x0, y0], [x1, y1], radius)?;
        output(&cuts, dest, capacity, required)
    })
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn rotating_sweep_and_tapered_stroke() {
        let a = ([0., 0.], 1.);
        let b = ([100., 0.], 1.);
        let cut = cut_segment(a, b, [50., -30.], [50., 30.], 4.).unwrap();
        assert!((cut.0 - 0.45).abs() < 1e-12 && (cut.1 - 0.55).abs() < 1e-12);
        let tap = cut_segment(a, ([100., 0.], 4.), [80., 2.], [80., 2.], 1.).unwrap();
        assert!(tap.0 < 0.80 && tap.1 > 0.80);
        assert!(cut_segment(a, b, [50., 7.], [50., 9.], 2.).is_none());
        let edge = cut_segment(a, b, [50., 3.], [50., 3.], 2.).unwrap();
        assert!((edge.0 - 0.5).abs() < 1e-12 && edge.0 == edge.1);
    }
}
