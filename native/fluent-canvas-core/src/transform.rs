use std::collections::HashSet;
use std::sync::Arc;

use crate::model::{reserve_tokens, StoredRecord};
use crate::{input, status, Core, Status};

#[repr(C)]
#[derive(Clone, Copy)]
pub struct GestureMatrix {
    xx: f64,
    xy: f64,
    yx: f64,
    yy: f64,
    dx: f64,
    dy: f64,
    width_scale: f64,
    width_low: f64,
    width_high: f64,
    height_scale: f64,
    height_low: f64,
    height_high: f64,
    coordinate_min: f64,
    coordinate_max: f64,
}

impl GestureMatrix {
    fn validate(&self) -> Result<(), Status> {
        let values = [
            self.xx,
            self.xy,
            self.yx,
            self.yy,
            self.dx,
            self.dy,
            self.width_scale,
            self.width_low,
            self.width_high,
            self.height_scale,
            self.height_low,
            self.height_high,
            self.coordinate_min,
            self.coordinate_max,
        ];
        if values.iter().any(|v| !v.is_finite())
            || self.width_scale <= 0.0
            || self.height_scale <= 0.0
            || self.width_low <= 0.0
            || self.width_low > self.width_high
            || self.height_low <= 0.0
            || self.height_low > self.height_high
            || self.coordinate_min >= self.coordinate_max
        {
            return Err(Status::Invalid);
        }
        Ok(())
    }

    // Records have a validated sample header. Version 1 render metadata begins
    // with its version, ARGB, then the two WPF dimensions. Other channels, pen
    // tip orientation and custom ISF metadata remain untouched.
    fn transformed(&self, mut record: Vec<u8>) -> Result<Arc<[u8]>, Status> {
        let count = u32::from_le_bytes(record[8..12].try_into().unwrap()) as usize;
        let end = 16 + count * 20;
        if record.get(end) != Some(&1) || record.len() < end + 21 {
            return Err(Status::Invalid);
        }
        for sample in record[16..end].chunks_exact_mut(20) {
            let x = read_double(sample, 0);
            let y = read_double(sample, 8);
            let new_x = x * self.xx + y * self.yx + self.dx;
            let new_y = x * self.xy + y * self.yy + self.dy;
            if !new_x.is_finite() || !new_y.is_finite() {
                return Err(Status::Invalid);
            }
            sample[..8].copy_from_slice(
                &new_x
                    .clamp(self.coordinate_min, self.coordinate_max)
                    .to_le_bytes(),
            );
            sample[8..16].copy_from_slice(
                &new_y
                    .clamp(self.coordinate_min, self.coordinate_max)
                    .to_le_bytes(),
            );
        }
        for (offset, scale, low, high) in [
            (end + 5, self.width_scale, self.width_low, self.width_high),
            (
                end + 13,
                self.height_scale,
                self.height_low,
                self.height_high,
            ),
        ] {
            let size = read_double(&record, offset);
            if !size.is_finite() || size <= 0.0 {
                return Err(Status::Invalid);
            }
            record[offset..offset + 8]
                .copy_from_slice(&(size * scale).clamp(low, high).to_le_bytes());
        }
        Ok(record.into())
    }

    fn place(&self, source: &StoredRecord, version: u64) -> Result<(StoredRecord, bool), Status> {
        if let Ok(brush) = source.brush() {
            let width = read_double(brush, 5);
            let height = read_double(brush, 13);
            if !width.is_finite() || !height.is_finite() || width <= 0.0 || height <= 0.0 {
                return Err(Status::Invalid);
            }
            if let Some(record) = source.placed(
                [self.xx, self.xy, self.yx, self.yy, self.dx, self.dy],
                self.coordinate_min,
                self.coordinate_max,
                (width * self.width_scale).clamp(self.width_low, self.width_high),
                (height * self.height_scale).clamp(self.height_low, self.height_high),
                version,
            ) {
                return Ok((record, true));
            }
        }
        // A clamped edit must bake its coordinates before later rotations.
        Ok((
            StoredRecord::from_base(self.transformed(source.bytes().into_owned())?, version),
            false,
        ))
    }
}

fn read_double(record: &[u8], offset: usize) -> f64 {
    f64::from_le_bytes(record[offset..offset + 8].try_into().unwrap())
}

/// # Safety
/// Pass a live exclusive page, a valid ID slice and a pointer to an aligned matrix.
#[no_mangle]
pub unsafe extern "C" fn fc_core_transform(
    core: *mut Core,
    ids: *const u64,
    count: usize,
    matrix: *const GestureMatrix,
) -> i32 {
    fc_core_place(core, ids, count, matrix, std::ptr::null_mut())
}

/// # Safety
/// Same inputs as fc_core_transform. Optional projection points to count writable u64s.
#[no_mangle]
pub unsafe extern "C" fn fc_core_place(
    core: *mut Core,
    ids: *const u64,
    count: usize,
    matrix: *const GestureMatrix,
    projection: *mut u64,
) -> i32 {
    status(|| {
        let core = core.as_mut().ok_or(Status::Invalid)?;
        let matrix = input(matrix, 1)?.first().ok_or(Status::Invalid)?;
        matrix.validate()?;
        let mut updates = Vec::with_capacity(count.min(1024));
        let mut seen = HashSet::new();
        let ids = input(ids, count)?;
        if !projection.is_null() && !projection.is_aligned() {
            return Err(Status::Invalid);
        }
        let first = reserve_tokens(count.max(1))?;
        for (i, &id) in ids.iter().enumerate() {
            if !seen.insert(id) {
                return Err(Status::Invalid);
            }
            let version = first + i as u64;
            let (record, fast) = matrix.place(core.record(id)?, version)?;
            if !projection.is_null() {
                projection.add(i).write(if fast { version } else { 0 });
            }
            updates.push((id, record));
        }
        core.commit_records(updates)
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    fn matrix() -> GestureMatrix {
        GestureMatrix {
            xx: 0.0,
            xy: 1.0,
            yx: -1.0,
            yy: 0.0,
            dx: 10.0,
            dy: 20.0,
            width_scale: 2.0,
            width_low: 0.1,
            width_high: 4.0,
            height_scale: 0.5,
            height_low: 0.1,
            height_high: 4.0,
            coordinate_min: -100.0,
            coordinate_max: 100.0,
        }
    }

    fn record(id: u64) -> Vec<u8> {
        let mut data = Vec::new();
        data.extend_from_slice(&id.to_le_bytes());
        data.extend_from_slice(&1u32.to_le_bytes());
        data.extend_from_slice(&23u32.to_le_bytes());
        data.extend_from_slice(&1.0f64.to_le_bytes());
        data.extend_from_slice(&3.0f64.to_le_bytes());
        data.extend_from_slice(&0.75f32.to_le_bytes());
        data.extend_from_slice(&[1, 255, 2, 3, 4]);
        data.extend_from_slice(&3.0f64.to_le_bytes());
        data.extend_from_slice(&2.0f64.to_le_bytes());
        data.extend_from_slice(&[21, 42]);
        data
    }

    #[test]
    fn affine_batch_preserves_pressure_and_other_channels_and_rolls_back() {
        let mut core = Core::new().unwrap();
        let original = record(7);
        core.append(&original).unwrap();
        let m = matrix();
        let invalid = [7, 8];
        let status = unsafe { fc_core_transform(&mut core, invalid.as_ptr(), invalid.len(), &m) };
        assert_ne!(status, 0);
        assert_eq!(core.read(7).unwrap().as_ref(), original);
        let ids = [7];
        assert_eq!(
            unsafe { fc_core_transform(&mut core, ids.as_ptr(), 1, &m) },
            0
        );
        let changed = core.read(7).unwrap();
        assert_eq!(
            (read_double(&changed, 16), read_double(&changed, 24)),
            (7.0, 21.0)
        );
        assert_eq!(&changed[32..41], &original[32..41]);
        assert_eq!(
            (read_double(&changed, 41), read_double(&changed, 49)),
            (4.0, 1.0)
        );
        assert_eq!(&changed[57..], &original[57..]);
        core.step(false).unwrap();
        assert_eq!(core.read(7).unwrap().as_ref(), original);
    }

    #[test]
    fn lazy_placement_exports_partial_ranges_and_clips_before_next_rotation() {
        let mut core = Core::new().unwrap();
        let mut ink = record(7);
        ink[12..16].copy_from_slice(&27u32.to_le_bytes());
        ink.truncate(16 + 20 + 21);
        ink.extend_from_slice(&[0, 0, 0, 0, 0, 0]);
        core.append(&ink).unwrap();
        let mut m = matrix();
        let mut version = 0;
        let id = 7;
        assert_eq!(
            unsafe { fc_core_place(&mut core, &id, 1, &m, &mut version) },
            0
        );
        assert_ne!(version, 0);
        let full = core.read(id).unwrap().into_owned();
        assert_eq!(
            full.as_slice(),
            m.transformed(ink.clone()).unwrap().as_ref()
        );
        let placed = core.record(id).unwrap();
        for offset in 0..full.len() {
            let mut part = [0u8; 11];
            let n = placed.copy_range(offset, &mut part);
            assert_eq!(&part[..n], &full[offset..offset + n]);
        }
        m.dx = 300.0;
        assert_eq!(
            unsafe { fc_core_place(&mut core, &id, 1, &m, &mut version) },
            0
        );
        assert_eq!(version, 0);
        let clamped = m.transformed(full.clone()).unwrap();
        assert_eq!(clamped.as_ref(), core.read(id).unwrap().as_ref());
        core.step(false).unwrap();
        assert_eq!(full.as_slice(), core.read(id).unwrap().as_ref());
    }
}
