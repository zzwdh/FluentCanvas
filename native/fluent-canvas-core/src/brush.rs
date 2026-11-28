// Post-collection ink styles from Fluent Canvas' original WPF handler.
// Render metadata, device channels and coordinates are left byte-for-byte intact.
const HEADER: usize = 16;
const SAMPLE: usize = 20;

fn position(record: &[u8], i: usize) -> (f64, f64) {
    let start = HEADER + i * SAMPLE;
    (
        f64::from_le_bytes(record[start..start + 8].try_into().unwrap()),
        f64::from_le_bytes(record[start + 8..start + 16].try_into().unwrap()),
    )
}

fn distance(a: (f64, f64), b: (f64, f64)) -> f64 {
    let dx = a.0 - b.0;
    let dy = a.1 - b.1;
    (dx * dx + dy * dy).sqrt()
}

fn speed_pressure(record: &[u8], i: usize, last: usize) -> f64 {
    let point = position(record, i);
    let before = position(record, i.saturating_sub(1));
    let after = position(record, (i + 1).min(last));
    let speed = (distance(before, point) + distance(after, point)) / 20.0;
    if speed >= 0.25 {
        0.5 - 0.3 * (speed.min(1.5) - 0.3) / 1.2
    } else if speed >= 0.05 {
        0.5
    } else {
        0.5 + 0.4 * (0.05 - speed) / 0.05
    }
}

// The source record has already passed model::parse_records.
pub(crate) fn finish(record: &[u8], style: i32, elapsed_ms: f64) -> Option<Vec<u8>> {
    let count = u32::from_le_bytes(record[8..12].try_into().unwrap()) as usize;
    if !matches!(style, 0 | 1 | 3) || (style == 0 && count == 2) || (style != 1 && count == 1) {
        return None;
    }
    let samples = &record[HEADER..HEADER + count * SAMPLE];
    if samples.chunks_exact(SAMPLE).any(|sample| {
        let pressure = f32::from_le_bytes(sample[16..20].try_into().unwrap()) as f64;
        pressure != 0.0 && !(0.5..=0.501).contains(&pressure)
    }) {
        return None;
    }

    let last = count - 1;
    let tail = match style {
        3 if elapsed_ms.is_finite() && elapsed_ms > 0.0 => {
            // WPF's touch timestamps are integer milliseconds. Preserve the
            // original integer interval, but bound the zero-interval cases.
            let interval = (elapsed_ms / count as f64).floor();
            if interval < 1.0 {
                last
            } else {
                ((1000.0 / interval) as usize).clamp(1, last)
            }
        }
        3 => 8.min(last),
        _ => 10.min(last),
    };
    let mut updated: Option<Vec<u8>> = None;
    for i in 0..count {
        let pressure = if style == 1 {
            speed_pressure(record, i, last)
        } else if last - i >= tail {
            0.5
        } else {
            0.4 * (last - i) as f64 / tail as f64 + 0.1
        };
        let start = HEADER + i * SAMPLE + 16;
        let bytes = (pressure as f32).to_le_bytes();
        if record[start..start + 4] != bytes {
            updated.get_or_insert_with(|| record.to_vec())[start..start + 4]
                .copy_from_slice(&bytes);
        }
    }
    updated
}
