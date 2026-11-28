use crate::{input, output, status, Core, Status};
use std::collections::{HashMap, HashSet};

type Id = [u8; 16];

/// Assets and live players are provided by the shell. All editable placement
/// fields and their stable identities belong to the same page as the ink.
#[repr(C)]
#[derive(Copy, Clone, Default, Debug)]
pub struct Object {
    id: Id,
    order: u64,
    kind: u32,
    z: i32,
    width: f64,
    height: f64,
    anchors: [f64; 4],
    opacity: f64,
    origin: [f64; 2],
    matrix: [f64; 6],
}

impl Object {
    fn check(&self) -> Result<(), Status> {
        if self.id == [0; 16]
            || !(1..=3).contains(&self.kind)
            || self.order == 0
            || [self.width, self.height]
                .iter()
                .any(|v| v.is_infinite() || *v < 0.0)
            || self.anchors.iter().any(|v| v.is_infinite())
            || !(0.0..=1.0).contains(&self.opacity)
            || self
                .origin
                .iter()
                .chain(self.matrix.iter())
                .any(|v| !v.is_finite())
        {
            return Err(Status::Invalid);
        }
        Ok(())
    }
}

#[repr(C)]
#[derive(Clone, Copy)]
pub struct Change {
    before: Object,
    after: Object,
}

#[derive(Clone, Default)]
pub(crate) struct Scene {
    pub(crate) id: Id,
    objects: HashMap<Id, Object>,
    pub(crate) pending: Vec<Change>,
}

impl Scene {
    fn prepare(&mut self, input: &[Change]) -> Result<(), Status> {
        if !self.pending.is_empty() {
            return Err(Status::Invalid);
        }
        let mut changes = Vec::with_capacity(input.len());
        let mut seen = HashSet::new();
        for change in input {
            let mut change = *change;
            let existed = change.before.kind != 0;
            let exists = change.after.kind != 0;
            if !existed && !exists {
                return Err(Status::Invalid);
            }
            if existed {
                change.before.check()?;
            }
            if exists {
                change.after.check()?;
            }
            let id = if existed {
                change.before.id
            } else {
                change.after.id
            };
            if !seen.insert(id)
                || (existed
                    && exists
                    && (id != change.after.id || change.before.kind != change.after.kind))
            {
                return Err(Status::Invalid);
            }
            if let Some(old) = self.objects.get(&id) {
                if !existed || old.kind != change.before.kind {
                    return Err(Status::Invalid);
                }
                // Capture transient WPF layout (such as an uncommitted control
                // resize) at the edit boundary, preserving the native object ID.
                change.before.order = old.order;
            }
            changes.push(change);
        }
        self.pending = changes;
        Ok(())
    }

    pub(crate) fn install(&mut self, changes: &[Change], forward: bool) {
        for change in changes {
            let (target, other) = if forward {
                (change.after, change.before)
            } else {
                (change.before, change.after)
            };
            if target.kind == 0 {
                self.objects.remove(&other.id);
            } else {
                self.objects.insert(target.id, target);
            }
        }
    }
}

/// # Safety
/// The page must be exclusive, and changes must contain count readable entries.
#[no_mangle]
pub unsafe extern "C" fn fc_scene_prepare(
    core: *mut Core,
    changes: *const Change,
    count: usize,
) -> i32 {
    status(|| {
        core.as_mut()
            .ok_or(Status::Invalid)?
            .scene
            .prepare(input(changes, count)?)
    })
}

/// # Safety
/// The page handle must be live and exclusive.
#[no_mangle]
pub unsafe extern "C" fn fc_scene_cancel(core: *mut Core) -> i32 {
    status(|| {
        core.as_mut().ok_or(Status::Invalid)?.scene.pending.clear();
        Ok(())
    })
}

/// # Safety
/// Buffers and the required size pointer must be live and writable.
#[no_mangle]
pub unsafe extern "C" fn fc_scene_objects(
    core: *const Core,
    out: *mut Object,
    capacity: usize,
    required: *mut usize,
) -> i32 {
    status(|| {
        let objects = &core.as_ref().ok_or(Status::Invalid)?.scene.objects;
        if required.is_null() || !required.is_aligned() {
            return Err(Status::Invalid);
        }
        required.write(objects.len());
        if capacity < objects.len() {
            return Err(Status::BufferTooSmall);
        }
        let mut entries: Vec<_> = objects.values().copied().collect();
        entries.sort_unstable_by_key(|value| value.order);
        output(&entries, out, capacity, required)
    })
}

/// # Safety
/// The page is live and id points to 16 readable bytes for set or writable bytes for get.
#[no_mangle]
pub unsafe extern "C" fn fc_scene_page_id(core: *mut Core, id: *mut Id, set: i32) -> i32 {
    status(|| {
        let scene = &mut core.as_mut().ok_or(Status::Invalid)?.scene;
        if id.is_null() {
            return Err(Status::Invalid);
        }
        match set {
            0 => id.write(scene.id),
            1 if id.read() != [0; 16] => scene.id = id.read(),
            _ => return Err(Status::Invalid),
        }
        Ok(())
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn placements_follow_the_ink_cursor_and_preserve_branches_on_failure() {
        let mut page = Core::new().unwrap();
        let object = Object {
            id: [1; 16],
            order: 1,
            kind: 1,
            width: 4.0,
            height: 3.0,
            opacity: 1.0,
            matrix: [1.0, 0.0, 0.0, 1.0, 0.0, 0.0],
            ..Object::default()
        };
        let insert = Change {
            before: Object::default(),
            after: object,
        };
        page.scene.prepare(&[insert]).unwrap();
        page.append(&[]).unwrap();
        let first = page.clone();
        let moved = Object {
            matrix: [0.0, 1.0, -1.0, 0.0, 2.0, 4.0],
            ..object
        };
        page.scene
            .prepare(&[Change {
                before: object,
                after: moved,
            }])
            .unwrap();
        page.append(&[]).unwrap();
        assert_eq!(first.scene.objects[&object.id].matrix, object.matrix);
        page.step(false).unwrap();
        let state = page.state();
        assert!(page.scene.prepare(&[insert]).is_err());
        assert_eq!(page.state(), state);
        assert_eq!(page.scene.objects[&object.id].matrix, object.matrix);
        page.step(true).unwrap();
        assert_eq!(page.scene.objects[&object.id].matrix, moved.matrix);
        page.step(false).unwrap();
        page.step(false).unwrap();
        assert!(page.scene.objects.is_empty());
    }
}
