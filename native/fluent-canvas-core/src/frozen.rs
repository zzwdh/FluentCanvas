use crate::model::StoredRecord;
use crate::{status, Core, Status};
use std::panic::{catch_unwind, AssertUnwindSafe};
use std::{ptr, slice};

// The immutable view keeps only the current stroke versions. It can outlive
// the original page and can be read by concurrent background writers.
pub struct FrozenPage {
    records: Vec<(usize, StoredRecord)>,
    length: usize,
}

impl FrozenPage {
    pub(crate) fn new(records: impl IntoIterator<Item = StoredRecord>) -> Self {
        let mut length = 0usize;
        let records = records
            .into_iter()
            .map(|record| {
                let start = length;
                length = length
                    .checked_add(record.len())
                    .expect("page size overflow");
                (start, record)
            })
            .collect();
        Self { records, length }
    }

    fn read(&self, offset: usize, output: &mut [u8]) -> Result<(), Status> {
        if offset > self.length || output.len() > self.length - offset {
            return Err(Status::Invalid);
        }
        if output.is_empty() {
            return Ok(());
        }
        let first = self.records.partition_point(|(start, _)| *start <= offset) - 1;
        let mut written = 0;
        for (start, record) in &self.records[first..] {
            written += record.copy_range(offset.saturating_sub(*start), &mut output[written..]);
            if written == output.len() {
                return Ok(());
            }
        }
        Err(Status::Invalid)
    }
}

/// # Safety
/// The core is live, and size points to writable, aligned memory.
#[no_mangle]
pub unsafe extern "C" fn fc_core_freeze(core: *const Core, size: *mut usize) -> *mut FrozenPage {
    catch_unwind(AssertUnwindSafe(|| {
        if core.is_null() || size.is_null() || !size.is_aligned() {
            return ptr::null_mut();
        }
        let page = (*core).freeze();
        size.write(page.length);
        Box::into_raw(Box::new(page))
    }))
    .unwrap_or(ptr::null_mut())
}

/// # Safety
/// The core is live, ids is readable for count entries, and size is writable.
#[no_mangle]
pub unsafe extern "C" fn fc_core_freeze_selection(
    core: *const Core,
    ids: *const u64,
    count: usize,
    size: *mut usize,
) -> *mut FrozenPage {
    catch_unwind(AssertUnwindSafe(|| -> Option<*mut FrozenPage> {
        let core = core.as_ref()?;
        if size.is_null() || !size.is_aligned() {
            return None;
        }
        let ids = crate::input(ids, count).ok()?;
        let records = ids
            .iter()
            .map(|id| core.record(*id).cloned())
            .collect::<Result<Vec<_>, _>>()
            .ok()?;
        let page = FrozenPage::new(records);
        size.write(page.length);
        Some(Box::into_raw(Box::new(page)))
    }))
    .ok()
    .flatten()
    .unwrap_or(ptr::null_mut())
}

/// # Safety
/// The immutable page handle must be live.
#[no_mangle]
pub unsafe extern "C" fn fc_frozen_record_size(page: *const FrozenPage, index: usize) -> usize {
    page.as_ref()
        .and_then(|page| page.records.get(index))
        .map_or(0, |(_, record)| record.len())
}

/// # Safety
/// The frozen handle is live, and dest is writable for length bytes.
#[no_mangle]
pub unsafe extern "C" fn fc_frozen_read(
    page: *const FrozenPage,
    offset: usize,
    dest: *mut u8,
    length: usize,
) -> i32 {
    status(|| {
        let page = page.as_ref().ok_or(Status::Invalid)?;
        if length == 0 {
            return page.read(offset, &mut []);
        }
        if dest.is_null() || length > isize::MAX as usize {
            return Err(Status::Invalid);
        }
        page.read(offset, slice::from_raw_parts_mut(dest, length))
    })
}

/// # Safety
/// Release an owned frozen handle exactly once.
#[no_mangle]
pub unsafe extern "C" fn fc_frozen_free(page: *mut FrozenPage) {
    if !page.is_null() {
        drop(Box::from_raw(page));
    }
}
