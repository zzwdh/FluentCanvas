//! Sparse editor entry points. See the crate-level exclusive handle and buffer contract.
use crate::*;

#[no_mangle]
pub unsafe extern "C" fn fc_core_update(
    core: *mut Core,
    records: *const u8,
    n: usize,
    brushes: *const u8,
    b: usize,
) -> i32 {
    status(|| {
        core.as_mut()
            .ok_or(Status::Invalid)?
            .update(input(records, n)?, input(brushes, b)?)
    })
}
#[no_mangle]
pub unsafe extern "C" fn fc_core_layers(
    core: *mut Core,
    removed: *const u64,
    r: usize,
    positions: *const u64,
    p: usize,
    records: *const u8,
    n: usize,
) -> i32 {
    status(|| {
        core.as_mut().ok_or(Status::Invalid)?.layers(
            input(removed, r)?,
            input(positions, p)?,
            input(records, n)?,
        )
    })
}
#[no_mangle]
pub unsafe extern "C" fn fc_core_position(core: *const Core, id: u64, position: *mut usize) -> i32 {
    status(|| {
        if position.is_null() || !position.is_aligned() {
            return Err(Status::Invalid);
        }
        position.write(
            core.as_ref()
                .ok_or(Status::Invalid)?
                .position(id)
                .ok_or(Status::Missing)?,
        );
        Ok(())
    })
}
#[no_mangle]
pub unsafe extern "C" fn fc_core_delta(
    core: *const Core,
    forward: i32,
    dest: *mut u64,
    capacity: usize,
    required: *mut usize,
) -> i32 {
    status(|| {
        output(
            &core.as_ref().ok_or(Status::Invalid)?.delta(forward != 0)?,
            dest,
            capacity,
            required,
        )
    })
}
#[no_mangle]
pub unsafe extern "C" fn fc_core_brush(
    core: *const Core,
    id: u64,
    dest: *mut u8,
    capacity: usize,
    required: *mut usize,
) -> i32 {
    status(|| {
        output(
            core.as_ref().ok_or(Status::Invalid)?.record(id)?.brush()?,
            dest,
            capacity,
            required,
        )
    })
}
#[no_mangle]
pub unsafe extern "C" fn fc_core_record_version(
    core: *const Core,
    id: u64,
    version: *mut u64,
) -> i32 {
    status(|| {
        if version.is_null() || !version.is_aligned() {
            return Err(Status::Invalid);
        }
        version.write(core.as_ref().ok_or(Status::Invalid)?.record(id)?.version());
        Ok(())
    })
}
#[no_mangle]
pub unsafe extern "C" fn fc_core_load_document(
    core: *mut Core,
    data: *const u8,
    len: usize,
    first: u64,
    undoable: i32,
) -> i32 {
    status(|| {
        core.as_mut()
            .ok_or(Status::Invalid)?
            .load_document(input(data, len)?, first, undoable != 0)
    })
}

#[no_mangle]
pub unsafe extern "C" fn fc_core_versions(core: *const Core, dest: *mut u64, count: usize) -> i32 {
    status(|| {
        let core = core.as_ref().ok_or(Status::Invalid)?;
        if count != core.order().len() || (count != 0 && (dest.is_null() || !dest.is_aligned())) {
            return Err(Status::Invalid);
        }
        for (i, &id) in core.order().iter().enumerate() {
            dest.add(i).write(core.record(id)?.version());
        }
        Ok(())
    })
}
