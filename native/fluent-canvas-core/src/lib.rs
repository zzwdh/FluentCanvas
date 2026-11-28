//! Native ink store C ABI. Each handle is exclusively owned and callers
//! serialize access, including reads, cloning and destruction. Buffer lengths
//! are element counts; every non-null pointer must denote a live, aligned
//! allocation of the specified size. Outputs must not alias other arguments.

mod brush;
mod document;
mod frozen;
mod hit;
mod model;
mod record;
mod scene;
mod shapes;
mod sparse;
mod transform;
pub use model::Core;

use std::panic::{catch_unwind, AssertUnwindSafe};
use std::{mem, ptr, slice};

#[derive(Debug, PartialEq, Eq)]
#[repr(i32)]
pub(crate) enum Status {
    Invalid = 1,
    Missing = 2,
    BufferTooSmall = 3,
    Panic = 4,
}

fn status(f: impl FnOnce() -> Result<(), Status>) -> i32 {
    match catch_unwind(AssertUnwindSafe(f)) {
        Ok(Ok(())) => 0,
        Ok(Err(reason)) => reason as i32,
        Err(_) => Status::Panic as i32,
    }
}

unsafe fn input<'a, T>(p: *const T, len: usize) -> Result<&'a [T], Status> {
    if len == 0 {
        return Ok(&[]);
    }
    if p.is_null() || !p.is_aligned() || len > isize::MAX as usize / mem::size_of::<T>() {
        return Err(Status::Invalid);
    }
    Ok(slice::from_raw_parts(p, len))
}

unsafe fn output<T: Copy>(
    src: &[T],
    dest: *mut T,
    capacity: usize,
    required: *mut usize,
) -> Result<(), Status> {
    if required.is_null() || !required.is_aligned() {
        return Err(Status::Invalid);
    }
    required.write(src.len());
    if src.len() > capacity {
        return Err(Status::BufferTooSmall);
    }
    if !src.is_empty() {
        if dest.is_null() || !dest.is_aligned() {
            return Err(Status::Invalid);
        }
        ptr::copy_nonoverlapping(src.as_ptr(), dest, src.len());
    }
    Ok(())
}

#[no_mangle]
pub extern "C" fn fc_core_abi_version() -> u32 {
    3
}

#[no_mangle]
pub extern "C" fn fc_core_new() -> *mut Core {
    Core::new().map_or(ptr::null_mut(), |core| Box::into_raw(Box::new(core)))
}

/// # Safety
/// Handle must be live or null. The returned handle has independent ownership.
#[no_mangle]
pub unsafe extern "C" fn fc_core_clone(core: *const Core) -> *mut Core {
    catch_unwind(AssertUnwindSafe(|| {
        core.as_ref().map_or(ptr::null_mut(), |core| {
            Box::into_raw(Box::new(core.clone()))
        })
    }))
    .unwrap_or(ptr::null_mut())
}

/// # Safety
/// Release an owned handle exactly once. Null is accepted.
#[no_mangle]
pub unsafe extern "C" fn fc_core_free(core: *mut Core) {
    if !core.is_null() {
        drop(Box::from_raw(core));
    }
}

/// # Safety
/// Pass a live exclusive handle, valid ID and byte buffers, or null buffers for zero lengths.
#[no_mangle]
pub unsafe extern "C" fn fc_core_apply(
    core: *mut Core,
    order: *const u64,
    count: usize,
    changes: *const u8,
    length: usize,
    reset: i32,
) -> i32 {
    status(|| {
        if !(0..=1).contains(&reset) {
            return Err(Status::Invalid);
        }
        core.as_mut().ok_or(Status::Invalid)?.apply(
            input(order, count)?,
            input(changes, length)?,
            reset == 1,
        )
    })
}

/// # Safety
/// Pass a live exclusive handle and a readable byte buffer or null for zero length.
#[no_mangle]
pub unsafe extern "C" fn fc_core_append(core: *mut Core, records: *const u8, length: usize) -> i32 {
    status(|| {
        core.as_mut()
            .ok_or(Status::Invalid)?
            .append(input(records, length)?)
    })
}

/// # Safety
/// Pass a live exclusive handle and a writable changed flag. Finishes a
/// collected stroke in place without advancing the history cursor.
#[no_mangle]
pub unsafe extern "C" fn fc_core_finish_stroke(
    core: *mut Core,
    id: u64,
    style: i32,
    elapsed_ms: f64,
    changed: *mut i32,
) -> i32 {
    status(|| {
        if changed.is_null() || !changed.is_aligned() {
            return Err(Status::Invalid);
        }
        let updated = core
            .as_mut()
            .ok_or(Status::Invalid)?
            .finish_stroke(id, style, elapsed_ms)?;
        changed.write(i32::from(updated));
        Ok(())
    })
}

/// # Safety
/// Pass a live exclusive handle.
#[no_mangle]
pub unsafe extern "C" fn fc_core_undo(core: *mut Core) -> i32 {
    status(|| core.as_mut().ok_or(Status::Invalid)?.step(false))
}

/// # Safety
/// Pass a live exclusive handle.
#[no_mangle]
pub unsafe extern "C" fn fc_core_redo(core: *mut Core) -> i32 {
    status(|| core.as_mut().ok_or(Status::Invalid)?.step(true))
}

/// # Safety
/// Pass a live exclusive handle and four distinct valid output pointers.
#[no_mangle]
pub unsafe extern "C" fn fc_core_state(
    core: *const Core,
    cursor: *mut usize,
    total: *mut usize,
    current: *mut u64,
    saved: *mut u64,
) -> i32 {
    status(|| {
        let state = core.as_ref().ok_or(Status::Invalid)?.state();
        if cursor.is_null()
            || total.is_null()
            || current.is_null()
            || saved.is_null()
            || !cursor.is_aligned()
            || !total.is_aligned()
            || !current.is_aligned()
            || !saved.is_aligned()
        {
            return Err(Status::Invalid);
        }
        cursor.write(state.0);
        total.write(state.1);
        current.write(state.2);
        saved.write(state.3);
        Ok(())
    })
}

/// # Safety
/// Pass a live exclusive handle.
#[no_mangle]
pub unsafe extern "C" fn fc_core_mark_saved(core: *mut Core) -> i32 {
    status(|| {
        core.as_mut().ok_or(Status::Invalid)?.mark_saved();
        Ok(())
    })
}

/// # Safety
/// Pass a live handle, output count pointer and writable ID buffer of capacity entries.
/// Pass null and zero capacity for a size query.
#[no_mangle]
pub unsafe extern "C" fn fc_core_order(
    core: *const Core,
    dest: *mut u64,
    capacity: usize,
    required: *mut usize,
) -> i32 {
    status(|| {
        output(
            core.as_ref().ok_or(Status::Invalid)?.order(),
            dest,
            capacity,
            required,
        )
    })
}

/// # Safety
/// As for fc_core_order, with a byte buffer. Returns the complete encoded record.
#[no_mangle]
pub unsafe extern "C" fn fc_core_read(
    core: *const Core,
    id: u64,
    dest: *mut u8,
    capacity: usize,
    required: *mut usize,
) -> i32 {
    status(|| {
        let record = core.as_ref().ok_or(Status::Invalid)?.record(id)?;
        if required.is_null() || !required.is_aligned() {
            return Err(Status::Invalid);
        }
        required.write(record.len());
        if record.len() > capacity {
            return Err(Status::BufferTooSmall);
        }
        if dest.is_null() {
            return Err(Status::Invalid);
        }
        record.copy_range(0, slice::from_raw_parts_mut(dest, record.len()));
        Ok(())
    })
}

/// # Safety
/// As for fc_core_read. Complete records are copied in display order without
/// allocating a second full page buffer in the core.
#[no_mangle]
pub unsafe extern "C" fn fc_core_export(
    core: *const Core,
    dest: *mut u8,
    capacity: usize,
    required: *mut usize,
) -> i32 {
    status(|| {
        let core = core.as_ref().ok_or(Status::Invalid)?;
        if required.is_null() || !required.is_aligned() {
            return Err(Status::Invalid);
        }
        let mut length = 0usize;
        for &id in core.order() {
            length = length
                .checked_add(core.record(id)?.len())
                .ok_or(Status::Invalid)?;
        }
        if length > isize::MAX as usize {
            return Err(Status::Invalid);
        }
        required.write(length);
        if capacity < length {
            return Err(Status::BufferTooSmall);
        }
        if length != 0 && dest.is_null() {
            return Err(Status::Invalid);
        }
        let mut offset = 0;
        for &id in core.order() {
            let record = core.record(id)?;
            record.copy_range(0, slice::from_raw_parts_mut(dest.add(offset), record.len()));
            offset += record.len();
        }
        Ok(())
    })
}

#[cfg(test)]
mod tests;
