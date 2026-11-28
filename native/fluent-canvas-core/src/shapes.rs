use crate::{input, output, status, Status};
use std::f64::consts::TAU;

pub(crate) const TOLERANCE: f64 = 0.25;
const DESCRIPTOR: usize = 41;
type Point = [f64; 2];

fn scalar(data: &[u8], offset: usize) -> f64 {
    f64::from_le_bytes(data[offset..offset + 8].try_into().unwrap())
}

const DIGITS: [&[Point]; 10] = [
    &[
        [0.25, 0.00],
        [0.75, 0.00],
        [1.00, 0.20],
        [1.00, 0.80],
        [0.75, 1.00],
        [0.25, 1.00],
        [0.00, 0.80],
        [0.00, 0.20],
        [0.25, 0.00],
    ],
    &[[0.15, 0.22], [0.55, 0.00], [0.55, 1.00]],
    &[
        [0.00, 0.20],
        [0.25, 0.00],
        [0.75, 0.00],
        [1.00, 0.20],
        [1.00, 0.35],
        [0.00, 1.00],
        [1.00, 1.00],
    ],
    &[
        [0.00, 0.08],
        [0.35, 0.00],
        [0.75, 0.00],
        [1.00, 0.18],
        [1.00, 0.33],
        [0.75, 0.47],
        [0.38, 0.47],
        [0.75, 0.47],
        [1.00, 0.65],
        [1.00, 0.82],
        [0.75, 1.00],
        [0.30, 1.00],
        [0.00, 0.91],
    ],
    &[[0.78, 1.00], [0.78, 0.00], [0.00, 0.66], [1.00, 0.66]],
    &[
        [1.00, 0.00],
        [0.14, 0.00],
        [0.00, 0.46],
        [0.68, 0.46],
        [1.00, 0.63],
        [1.00, 0.82],
        [0.74, 1.00],
        [0.26, 1.00],
        [0.00, 0.88],
    ],
    &[
        [0.95, 0.08],
        [0.58, 0.00],
        [0.20, 0.17],
        [0.00, 0.48],
        [0.00, 0.79],
        [0.27, 1.00],
        [0.73, 1.00],
        [1.00, 0.79],
        [1.00, 0.62],
        [0.74, 0.45],
        [0.27, 0.45],
        [0.00, 0.58],
    ],
    &[[0.00, 0.00], [1.00, 0.00], [0.28, 1.00]],
    &[
        [0.50, 0.47],
        [0.95, 0.28],
        [0.95, 0.15],
        [0.72, 0.00],
        [0.28, 0.00],
        [0.05, 0.15],
        [0.05, 0.28],
        [0.50, 0.47],
        [1.00, 0.69],
        [1.00, 0.83],
        [0.75, 1.00],
        [0.25, 1.00],
        [0.00, 0.83],
        [0.00, 0.69],
        [0.50, 0.47],
    ],
    &[
        [1.00, 0.42],
        [0.73, 0.55],
        [0.26, 0.55],
        [0.00, 0.38],
        [0.00, 0.20],
        [0.27, 0.00],
        [0.73, 0.00],
        [1.00, 0.20],
        [1.00, 0.52],
        [0.80, 0.83],
        [0.42, 1.00],
        [0.05, 0.92],
    ],
];

// Coordinates are expressed in the frame made by three absolute control points.
// Affine edits can transform those controls without rewriting a sampled contour.
fn project(descriptor: &[u8], [a, b, c]: [Point; 3], tolerance: f64) -> Result<Vec<Point>, Status> {
    if descriptor.len() != DESCRIPTOR
        || !(tolerance > 0.0 && tolerance.is_finite())
        || [a, b, c].iter().flatten().any(|v| !v.is_finite())
    {
        return Err(Status::Invalid);
    }
    let parameter = scalar(descriptor, 1);
    let width = scalar(descriptor, 9);
    if !parameter.is_finite() || !width.is_finite() {
        return Err(Status::Invalid);
    }
    let part = i32::from_le_bytes(descriptor[17..21].try_into().unwrap());
    let ticks = i32::from_le_bytes(descriptor[21..25].try_into().unwrap());
    let frame = |x: f64, y: f64| {
        [
            a[0] + x * (b[0] - a[0]) + y * (c[0] - a[0]),
            a[1] + x * (b[1] - a[1]) + y * (c[1] - a[1]),
        ]
    };
    let points = match descriptor[0] {
        1 | 4 | 5 => {
            if parameter.abs() > TAU + 1e-12 {
                return Err(Status::Invalid);
            }
            // Frobenius norm bounds the radius even after a shear.
            let radius = (b[0] - a[0])
                .hypot(b[1] - a[1])
                .hypot((c[0] - a[0]).hypot(c[1] - a[1]));
            let step = 2.0 * (1.0 - tolerance / radius.max(tolerance)).acos();
            let mut segments = (parameter.abs() / step).ceil().clamp(2.0, 8192.0) as usize;
            let closed = (parameter.abs() - TAU).abs() < 1e-12;
            if closed {
                segments = segments.next_multiple_of(4);
            }
            let mut points = Vec::with_capacity(segments + 3);
            if descriptor[0] == 4 && !closed {
                points.push(a);
            }
            for i in 0..=segments {
                let angle = parameter * i as f64 / segments as f64;
                points.push(if i == 0 || (closed && i == segments) {
                    b
                } else {
                    frame(angle.cos(), angle.sin())
                });
            }
            if descriptor[0] == 4 && !closed {
                points.push(a);
            }
            if descriptor[0] == 5 {
                points.push(b);
            }
            points
        }
        2 => vec![a, b, c, a],
        3 => vec![
            a,
            b,
            frame(1.0 - parameter, 1.0),
            b,
            frame(1.0 - parameter, -1.0),
        ],
        6 => {
            if !(1..=9).contains(&ticks) || width <= 0.0 || parameter <= 0.0 {
                return Err(Status::Invalid);
            }
            let arrow = (width * 0.12).clamp(9.0, 18.0);
            let half = width / 2.0 - arrow - parameter;
            if half <= 0.0 {
                return Err(Status::Invalid);
            }
            let spacing = half / f64::from(ticks);
            let tick_half = (parameter * 0.4).max(4.0);
            let pt = |x: f64, y: f64| frame(x / width, y / width);
            if part == 0 {
                vec![
                    a,
                    b,
                    pt(width - arrow, -arrow * 0.34),
                    b,
                    pt(width - arrow, arrow * 0.34),
                ]
            } else {
                let (piece, t) = match part {
                    1..=19 => (0, part - 10),
                    21..=39 => (1, part - 30),
                    41..=49 => (2, part - 50),
                    _ => return Err(Status::Invalid),
                };
                if t.abs() > ticks {
                    return Err(Status::Invalid);
                }
                let x = width / 2.0 + f64::from(t) * spacing;
                let h = if t == 0 { tick_half * 1.45 } else { tick_half };
                if piece == 0 {
                    vec![pt(x, -h), pt(x, h)]
                } else {
                    let dh = (spacing * 0.44).clamp(10.0, 16.0);
                    let dw = dh * 0.58;
                    let y = tick_half * 1.45 + parameter / 2.0 + 4.0;
                    let sw = dw * 0.55;
                    let gap = dw * 0.28;
                    let left = x - dw / 2.0 + if t < 0 { (sw + gap) / 2.0 } else { 0.0 };
                    if piece == 1 {
                        DIGITS[t.unsigned_abs() as usize]
                            .iter()
                            .map(|p| pt(left + p[0] * dw, y + p[1] * dh))
                            .collect()
                    } else {
                        vec![
                            pt(left - gap - sw, y + dh * 0.55),
                            pt(left - gap, y + dh * 0.55),
                        ]
                    }
                }
            }
        }
        _ => return Err(Status::Invalid),
    };
    if points.iter().flatten().any(|v| !v.is_finite()) {
        return Err(Status::Invalid);
    }
    Ok(points)
}

pub(crate) fn project_record(record: &[u8], tolerance: f64) -> Result<Option<Vec<Point>>, Status> {
    if record.len() < 16 {
        return Err(Status::Invalid);
    }
    let render_len = u32::from_le_bytes(record[12..16].try_into().unwrap()) as usize;
    let render = &record[record
        .len()
        .checked_sub(render_len)
        .ok_or(Status::Invalid)?..];
    // Other render payload versions may be supplied by non-WPF callers.
    if render.first() != Some(&1) {
        return Ok(None);
    }
    let offset = if render.get(25) == Some(&1) { 74 } else { 26 };
    let flags = render.get(offset).copied().unwrap_or_default();
    if flags & 4 == 0 {
        return Ok(None);
    }
    if flags & 1 != 0 || record.len() - render_len != 16 + 60 {
        return Err(Status::Invalid);
    }
    let descriptor = render
        .get(offset + 1..offset + 1 + DESCRIPTOR)
        .ok_or(Status::Invalid)?;
    let anchors =
        std::array::from_fn(|i| [scalar(record, 16 + 20 * i), scalar(record, 24 + 20 * i)]);
    project(descriptor, anchors, tolerance).map(Some)
}

/// # Safety
/// The encoded descriptor and three XY anchors occupy exactly 89 readable bytes.
/// Output and count must be valid for the requested capacities, with no overlap.
#[no_mangle]
pub unsafe extern "C" fn fc_shape_project(
    data: *const u8,
    len: usize,
    tolerance: f64,
    dest: *mut f64,
    capacity: usize,
    count: *mut usize,
) -> i32 {
    status(|| {
        if len != DESCRIPTOR + 48 {
            return Err(Status::Invalid);
        }
        let data = input(data, len)?;
        let points = project(
            &data[..DESCRIPTOR],
            std::array::from_fn(|i| {
                [
                    scalar(data, DESCRIPTOR + 16 * i),
                    scalar(data, DESCRIPTOR + 16 * i + 8),
                ]
            }),
            tolerance,
        )?;
        output(points.as_flattened(), dest, capacity, count)
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn affine_ellipse_preserves_extrema_and_subdivides_to_requested_error() {
        let mut descriptor = [0; DESCRIPTOR];
        descriptor[0] = 1;
        descriptor[1..9].copy_from_slice(&TAU.to_le_bytes());
        let frame = [[10., 20.], [100., 35.], [40., 50.]];
        let projected = project(&descriptor, frame, TOLERANCE).unwrap();
        let sides = projected.len() - 1;
        assert_eq!(projected.first(), projected.last());
        assert!((projected[sides / 4][0] - frame[2][0]).abs() < 1e-10);
        for (i, edge) in projected.windows(2).enumerate() {
            let angle = (i as f64 + 0.5) * TAU / sides as f64;
            let dx = 10. + angle.cos() * 90. + angle.sin() * 30. - (edge[0][0] + edge[1][0]) / 2.;
            let dy = 20. + angle.cos() * 15. + angle.sin() * 30. - (edge[0][1] + edge[1][1]) / 2.;
            assert!(dx.hypot(dy) <= TOLERANCE);
        }
        descriptor[1..9].copy_from_slice(&f64::NAN.to_le_bytes());
        assert!(project(&descriptor, frame, TOLERANCE).is_err());
    }
}
