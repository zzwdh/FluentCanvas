use super::*;

fn record(id: u64, pressure: f32, extra: u8) -> Vec<u8> {
    let mut r = Vec::new();
    r.extend(id.to_le_bytes());
    r.extend(1u32.to_le_bytes());
    r.extend(2u32.to_le_bytes());
    r.extend((-12.5f64).to_le_bytes());
    r.extend(6.75f64.to_le_bytes());
    r.extend(pressure.to_le_bytes());
    r.extend([0, extra]);
    r
}

#[test]
fn edit_shapes_erase_reorder_and_branch_preserve_payloads() {
    let mut c = Core::new().unwrap();
    let (a, b, d) = (record(7, 0.9, 3), record(11, 0.0, 1), record(9, 1.0, 4));
    c.apply(&[7, 11], &[a.clone(), b.clone()].concat(), true)
        .unwrap();
    let origin = c.state().2;
    assert_eq!(origin, c.state().3);
    c.append(&d).unwrap();
    c.mark_saved();
    let saved = c.state().3;
    let b2 = record(11, 0.6, 5);
    c.apply(&[9, 11], &b2, false).unwrap();
    assert_eq!(c.order(), [9, 11]);
    assert_eq!(c.read(7), Err(Status::Missing));
    assert_eq!(c.read(11).unwrap(), b2);
    c.step(false).unwrap();
    assert_eq!(c.state(), (1, 2, saved, saved));
    assert_eq!(c.order(), [7, 11, 9]);
    assert_eq!(c.read(7).unwrap(), a);
    assert_eq!(c.read(11).unwrap(), b);
    c.step(false).unwrap();
    assert_eq!(c.state(), (0, 2, origin, saved));
    assert_eq!(c.step(false), Err(Status::Missing));
    c.step(true).unwrap();
    c.step(true).unwrap();
    assert_eq!(c.read(11).unwrap(), b2);
    c.step(false).unwrap();
    c.apply(&[], &[], false).unwrap();
    assert_eq!(c.step(true), Err(Status::Missing));
    c.step(false).unwrap();
    assert_eq!(c.order(), [7, 11, 9]);
}

#[test]
fn empty_element_edits_advance_history_and_clones_isolate_branches() {
    let mut page = Core::new().unwrap();
    let other = Core::new().unwrap();
    assert_ne!(page.state().2, other.state().2);
    page.append(&record(1, 0.5, 0)).unwrap();
    page.append(&[]).unwrap();
    page.mark_saved();
    let frozen = page.clone();
    page.step(false).unwrap();
    page.append(&[]).unwrap();
    assert_ne!(page.state().2, frozen.state().2);
    assert_eq!(frozen.state().2, frozen.state().3);
    page.apply(&[], &[], true).unwrap();
    assert_eq!(page.state().0, 0);
    assert_eq!(page.state().1, 0);
    assert!(page.order().is_empty());
    assert_eq!(frozen.order(), [1]);
}

#[test]
fn post_collection_fixup_tracks_its_version_across_unrelated_edits() {
    let mut c = Core::new().unwrap();
    let original = record(5, 0.5, 9);
    let styled = record(5, 0.9, 9);
    let later = record(5, 0.25, 1);
    c.append(&original).unwrap();
    c.append(&[]).unwrap();
    c.mark_saved();
    c.apply(&[5], &later, false).unwrap();
    c.step(false).unwrap();
    let snapshot = c.clone();
    assert!(c.finish_stroke(5, 1, 0.0).unwrap());
    assert_ne!(c.state().2, c.state().3);
    assert_eq!(c.read(5).unwrap(), styled);
    c.step(true).unwrap();
    assert_eq!(c.read(5).unwrap(), later);
    c.step(false).unwrap();
    assert_ne!(c.state().2, c.state().3);
    c.step(false).unwrap();
    assert_eq!(c.read(5).unwrap(), styled);
    c.step(false).unwrap();
    assert_eq!(c.read(5), Err(Status::Missing));
    c.step(true).unwrap();
    assert_eq!(c.read(5).unwrap(), styled);
    assert_eq!(snapshot.read(5).unwrap(), original);
}

#[test]
fn malformed_transactions_are_atomic_and_preserve_redo() {
    let mut c = Core::new().unwrap();
    let a = record(1, 0.75, 3);
    c.append(&a).unwrap();
    c.append(&record(2, 0.5, 0)).unwrap();
    c.step(false).unwrap();
    let before = c.state();
    let mut bad_coord = record(3, 0.75, 1);
    bad_coord[16..24].copy_from_slice(&f64::INFINITY.to_le_bytes());
    let mut bad_count = a.clone();
    bad_count[8..12].copy_from_slice(&u32::MAX.to_le_bytes());
    for bytes in [
        vec![1],
        a[..a.len() - 1].to_vec(),
        record(0, 1.0, 4),
        bad_coord,
        bad_count,
        record(1, f32::NAN, 2),
        record(1, -0.1, 5),
        [a.clone(), a.clone()].concat(),
    ] {
        assert_eq!(c.apply(&[1], &bytes, false), Err(Status::Invalid));
    }
    assert_eq!(c.apply(&[1, 1], &[], false), Err(Status::Invalid));
    assert_eq!(c.apply(&[1, 3], &[], true), Err(Status::Invalid));
    assert_eq!(c.apply(&[], &a, false), Err(Status::Invalid));
    assert_eq!(c.append(&a), Err(Status::Invalid));
    assert_eq!(c.finish_stroke(9, 1, 0.0), Err(Status::Missing));
    assert_eq!(c.state(), before);
    assert_eq!(c.read(1).unwrap(), a);
    c.step(true).unwrap();
    assert_eq!(c.order(), [1, 2]);
}

#[test]
fn ffi_reports_exact_output_sizes_without_partial_writes() {
    unsafe {
        let c = fc_core_new();
        let r = record(14, 0.4, 7);
        assert_eq!(fc_core_abi_version(), 3);
        assert_eq!(fc_core_append(c, ptr::null(), 4), 1);
        assert_eq!(fc_core_append(c, r.as_ptr(), r.len()), 0);
        let mut size = 0;
        assert_eq!(fc_core_read(c, 14, ptr::null_mut(), 0, &mut size), 3);
        assert_eq!(size, r.len());
        let mut out = vec![42; size];
        assert_eq!(
            fc_core_read(c, 14, out.as_mut_ptr(), size - 1, &mut size),
            3
        );
        assert!(out.iter().all(|&x| x == 42));
        assert_eq!(
            fc_core_read(c, 14, out.as_mut_ptr(), out.len(), &mut size),
            0
        );
        assert_eq!(out, r);
        let clone = fc_core_clone(c);
        fc_core_free(c);
        let mut id = 0;
        assert_eq!(fc_core_order(clone, &mut id, 1, &mut size), 0);
        assert_eq!((id, size), (14, 1));
        assert_eq!(fc_core_undo(clone), 0);
        assert_eq!(fc_core_order(clone, ptr::null_mut(), 0, &mut size), 0);
        assert_eq!(size, 0);
        assert_eq!(fc_core_undo(clone), 2);
        assert_eq!(fc_core_redo(clone), 0);
        fc_core_free(clone);
        fc_core_free(ptr::null_mut());
    }
}

#[test]
fn export_writes_complete_records_in_display_order() {
    unsafe {
        let mut c = Core::new().unwrap();
        let mut size = usize::MAX;
        assert_eq!(fc_core_export(&c, ptr::null_mut(), 0, &mut size), 0);
        assert_eq!(size, 0);
        let a = record(1, 0.25, 8);
        let b = record(3, 0.75, 4);
        c.append(&[a.clone(), b.clone()].concat()).unwrap();
        c.apply(&[3, 1], &[], false).unwrap();
        assert_eq!(fc_core_export(&c, ptr::null_mut(), 0, &mut size), 3);
        assert_eq!(size, b.len() + a.len());
        let mut result = vec![251; size];
        assert_eq!(
            fc_core_export(&c, result.as_mut_ptr(), size - 1, &mut size),
            3
        );
        assert!(result.iter().all(|&byte| byte == 251));
        assert_eq!(fc_core_export(&c, result.as_mut_ptr(), size, &mut size), 0);
        assert_eq!(result, [b, a].concat());
    }
}

fn samples(points: &[(f64, f32)]) -> Vec<u8> {
    let mut bytes = record(15, 0.5, 9);
    bytes.truncate(16);
    bytes[8..12].copy_from_slice(&(points.len() as u32).to_le_bytes());
    for &(x, pressure) in points {
        bytes.extend(x.to_le_bytes());
        bytes.extend(12.25f64.to_le_bytes());
        bytes.extend(pressure.to_le_bytes());
    }
    bytes.extend([0, 9]);
    bytes
}

fn press(record: &[u8], index: usize) -> f32 {
    let offset = 32 + index * 20;
    f32::from_le_bytes(record[offset..offset + 4].try_into().unwrap())
}

#[test]
fn finishing_a_stroke_preserves_hardware_pressure_and_short_strokes() {
    let mut c = Core::new().unwrap();
    for (points, style, changed) in [
        (vec![(0.0, 0.5), (1.0, 0.35), (4.0, 0.5)], 0, false),
        (vec![(0.0, 0.5), (1.0, 0.5)], 0, false),
        (vec![(0.0, 0.5)], 3, false),
        (vec![(0.0, 0.0)], 1, true),
        (vec![(0.0, 0.5), (4.0, 0.5)], 2, false),
    ] {
        let bytes = samples(&points);
        c.apply(&[15], &bytes, true).unwrap();
        assert_eq!(c.finish_stroke(15, style, 0.0).unwrap(), changed);
        if changed {
            assert_eq!(press(&c.read(15).unwrap(), 0), 0.9);
        } else {
            assert_eq!(c.read(15).unwrap(), bytes);
        }
    }
}

#[test]
fn tail_and_speed_brushes_update_samples_and_keep_one_undo_step() {
    let mut c = Core::new().unwrap();
    let points: Vec<_> = (0..13).map(|i| (i as f64 * 3.0, 0.5)).collect();
    let data = samples(&points);
    c.append(&data).unwrap();
    assert!(c.finish_stroke(15, 0, 0.0).unwrap());
    assert_eq!(c.state().0, 1);
    let stroke = c.read(15).unwrap();
    assert_eq!(press(&stroke, 2), 0.5);
    assert_eq!(press(&stroke, 7), 0.3);
    assert_eq!(press(&stroke, 12), 0.1);
    assert_eq!(&stroke[stroke.len() - 2..], &[0, 9]);
    c.step(false).unwrap();
    c.step(true).unwrap();
    assert_eq!(press(&c.read(15).unwrap(), 12), 0.1);
    c.apply(&[15], &data, true).unwrap();
    assert!(c.finish_stroke(15, 3, 6500.0).unwrap());
    assert_eq!(press(&c.read(15).unwrap(), 10), 0.5);
    assert_eq!(press(&c.read(15).unwrap(), 11), 0.3);
    assert_eq!(press(&c.read(15).unwrap(), 12), 0.1);
    c.apply(
        &[15],
        &samples(&[(0.0, 0.5), (15.0, 0.5), (30.0, 0.5)]),
        true,
    )
    .unwrap();
    assert!(c.finish_stroke(15, 1, 0.0).unwrap());
    assert_eq!(press(&c.read(15).unwrap(), 1), 0.2);
}

#[test]
fn computed_pressure_revises_a_saved_baseline_and_bounds_degenerate_timestamps() {
    let mut c = Core::new().unwrap();
    let data = samples(&[(0.0, 0.5), (1.0, 0.5), (2.0, 0.5)]);
    for elapsed in [1.0, 1_000_000.0, 0.0, -50.0, f64::NAN, f64::INFINITY] {
        c.apply(&[15], &data, true).unwrap();
        c.append(&[]).unwrap();
        c.step(false).unwrap();
        let old = c.clone();
        assert!(c.finish_stroke(15, 3, elapsed).unwrap());
        assert_eq!(c.state().0, 0);
        assert_ne!(c.state().2, c.state().3);
        assert_eq!(c.state().3, old.state().3);
        assert_eq!(old.read(15).unwrap(), data);
        let output = c.read(15).unwrap().to_vec();
        assert_eq!(press(&output, 2), 0.1);
        c.step(true).unwrap();
        assert_eq!(c.read(15).unwrap(), output);
        c.step(false).unwrap();
        assert_ne!(c.state().2, c.state().3);
    }
}

fn versioned_record(id: u64) -> Vec<u8> {
    let mut bytes = record(id, 0.5, 0);
    bytes.truncate(36);
    let mut brush = vec![0; 26];
    brush[0] = 1;
    bytes[12..16].copy_from_slice(&28u32.to_le_bytes());
    bytes.extend(brush);
    bytes.extend([0, 17]);
    bytes
}

#[test]
fn ten_thousand_layer_page_returns_one_sparse_brush_delta_and_shares_geometry() {
    let mut page = Core::new().unwrap();
    let all: Vec<_> = (1..=10_000).flat_map(versioned_record).collect();
    page.append(&all).unwrap();
    let prior = page.clone();
    let target = 4_952u64;
    let mut new_brush = vec![0; 74];
    new_brush[0] = 1;
    new_brush[2] = 0xbc;
    new_brush[25] = 1;
    let mut edit = Vec::new();
    edit.extend(target.to_le_bytes());
    edit.extend((new_brush.len() as u32).to_le_bytes());
    edit.extend(&new_brush);
    page.update(&[], &edit).unwrap();
    assert!(prior
        .record(target)
        .unwrap()
        .shares_geometry(page.record(target).unwrap()));
    assert_eq!(page.record(target).unwrap().brush().unwrap(), new_brush);
    let frozen = page.read(target).unwrap().into_owned();
    assert_eq!(&frozen[36..110], &new_brush);
    assert_eq!(frozen[111], 17);
    assert_eq!(u32::from_le_bytes(frozen[12..16].try_into().unwrap()), 76);
    for (i, &byte) in frozen.iter().enumerate() {
        let mut out = [0];
        assert_eq!(page.record(target).unwrap().copy_range(i, &mut out), 1);
        assert_eq!(out[0], byte);
    }
    page.step(false).unwrap();
    assert_eq!(page.delta(false).unwrap(), [4, target - 1, target]);
    assert_eq!(page.read(target).unwrap(), prior.read(target).unwrap());
    page.step(true).unwrap();
    assert_eq!(page.delta(true).unwrap(), [4, target - 1, target]);
    assert_eq!(page.read(target).unwrap(), frozen);
}

#[test]
fn sparse_layer_edits_track_disjoint_positions_and_reverse_only_changed_strokes() {
    let mut page = Core::new().unwrap();
    page.append(&(1..=10).flat_map(versioned_record).collect::<Vec<_>>())
        .unwrap();
    let original = page.order().to_vec();
    page.layers(
        &[2, 8],
        &[1, 7],
        &[versioned_record(11), versioned_record(12)].concat(),
    )
    .unwrap();
    assert_eq!(page.order(), &[1, 11, 3, 4, 5, 6, 7, 12, 9, 10]);
    page.step(false).unwrap();
    assert_eq!(page.order(), original);
    assert_eq!(
        page.delta(false).unwrap(),
        [1, 7, 12, 1, 1, 11, 2, 1, 2, 2, 7, 8]
    );
    page.step(true).unwrap();
    assert_eq!(
        page.delta(true).unwrap(),
        [1, 7, 8, 1, 1, 2, 2, 1, 11, 2, 7, 12]
    );
    let snapshot = page.state();
    assert_eq!(page.layers(&[3, 3], &[], &[]), Err(Status::Invalid));
    assert_eq!(
        page.layers(
            &[3],
            &[0, 0],
            &[versioned_record(13), versioned_record(14)].concat()
        ),
        Err(Status::Invalid)
    );
    assert_eq!(page.state(), snapshot);
    for (i, id) in page.order().iter().enumerate() {
        assert_eq!(page.position(*id), Some(i));
    }
}

#[test]
fn native_import_remaps_file_ids_and_discards_invalid_documents_atomically() {
    let mut page = Core::new().unwrap();
    let bytes = [versioned_record(u64::MAX), versioned_record(10)].concat();
    page.load_document(&bytes, 3, true).unwrap();
    assert_eq!(page.order(), [3, 4]);
    assert_eq!(page.state().0, 1);
    assert_eq!(&page.read(3).unwrap()[..8], &3u64.to_le_bytes());
    let before = page.state();
    assert_eq!(
        page.load_document(
            &[versioned_record(13), versioned_record(13)].concat(),
            7,
            false
        ),
        Err(Status::Invalid)
    );
    assert_eq!(page.state(), before);
    page.step(false).unwrap();
    assert!(page.order().is_empty());
}
