//! Reversible, block-local layout for FCINK v2. Coordinates are never quantized.
//! Repeated brush and packet descriptors share a dictionary. XOR byte planes
//! expose repetition across samples to the stream compressor.
use crate::{input, output, status, Status};
use std::collections::HashMap;

const BLOCK: usize = 256 * 1024;
const SAMPLE: usize = 20;
const DECIMAL: usize = 1 << 31;

// Digitizers and exported InkML often use decimal coordinates. Use a delta
// only when converting it back gives the identical IEEE bits, including -0.
fn grid(bytes: &[u8]) -> Option<i64> {
    let x = f64::from_le_bytes(bytes.try_into().unwrap());
    let scaled = (x * 100.).round();
    let value = scaled as i64;
    if scaled.abs() <= 1_000_000_000. && (value as f64 / 100.).to_bits() == x.to_bits() {
        Some(value)
    } else {
        None
    }
}

fn encode_axis(out: &mut Vec<u8>, bytes: &[u8], previous: &mut i64) {
    if let Some(value) = grid(bytes) {
        let delta = value - *previous;
        *previous = value;
        let mut code = (((delta << 1) ^ (delta >> 63)) as u32) + 1;
        while code >= 128 {
            out.push((code as u8 & 127) | 128);
            code >>= 7;
        }
        out.push(code as u8);
    } else {
        out.push(0);
        out.extend_from_slice(bytes);
    }
}

fn decode_axis(input: &mut Reader<'_>, previous: &mut i64) -> Result<[u8; 8], Status> {
    let mut code = 0u64;
    for shift in (0..35).step_by(7) {
        let byte = input.take(1)?[0];
        code |= u64::from(byte & 127) << shift;
        if byte < 128 {
            if code == 0 {
                return Ok(input.take(8)?.try_into().unwrap());
            }
            let zig = code - 1;
            let value = *previous + ((zig >> 1) as i64 ^ -((zig & 1) as i64));
            if value.abs() > 1_000_000_000 {
                return Err(Status::Invalid);
            }
            *previous = value;
            return Ok((value as f64 / 100.).to_le_bytes());
        }
    }
    Err(Status::Invalid)
}

struct Reader<'a>(&'a [u8]);
impl<'a> Reader<'a> {
    fn take(&mut self, n: usize) -> Result<&'a [u8], Status> {
        let (value, rest) = self.0.split_at_checked(n).ok_or(Status::Invalid)?;
        self.0 = rest;
        Ok(value)
    }
    fn number(&mut self) -> Result<usize, Status> {
        Ok(u32::from_le_bytes(self.take(4)?.try_into().unwrap()) as usize)
    }
}

fn number(out: &mut Vec<u8>, value: usize) {
    out.extend_from_slice(&(value as u32).to_le_bytes());
}

fn prefix_len(render: &[u8]) -> Option<usize> {
    if render.first() != Some(&1) {
        return None;
    }
    let flags_at = match render.get(25)? {
        0 => 26,
        1 => 74,
        _ => return None,
    };
    let flags = *render.get(flags_at)?;
    let start = flags_at + 1;
    if flags & 1 == 0 {
        return Some(start);
    }
    let channels = u32::from_le_bytes(render.get(start..start + 4)?.try_into().ok()?) as usize;
    (start + 4)
        .checked_add(channels.checked_mul(33)?)
        .filter(|end| *end <= render.len())
}

fn pack(bytes: &[u8]) -> Result<Vec<u8>, Status> {
    if bytes.len() > BLOCK {
        return Err(Status::Invalid);
    }
    let mut input = Reader(bytes);
    let mut dictionary = Vec::new();
    let mut indices = HashMap::new();
    let mut descriptors = Vec::new();
    let mut samples = Vec::new();
    let mut count = 0;
    while !input.0.is_empty() {
        let id = input.take(8)?;
        let n = input.number()?;
        let render_size = input.number()?;
        if n == 0 || n > BLOCK / SAMPLE {
            return Err(Status::Invalid);
        }
        let points = input.take(n * SAMPLE)?;
        let render = input.take(render_size)?;
        let prefix = prefix_len(render).ok_or(Status::Invalid)?;
        let (brush, tail) = render.split_at(prefix);
        let index = *indices.entry(brush).or_insert_with(|| {
            let index = dictionary.len();
            dictionary.push(brush);
            index
        });
        descriptors.extend_from_slice(id);
        number(&mut descriptors, n);
        number(&mut descriptors, index);
        number(&mut descriptors, tail.len());
        descriptors.extend_from_slice(tail);
        samples.push(points);
        count += n;
    }
    let mut out = Vec::with_capacity(bytes.len());
    let mut tested = 0;
    let mut exact = 0;
    for point in samples.iter().flat_map(|p| p.chunks_exact(SAMPLE)).take(96) {
        for axis in point[..16].chunks_exact(8) {
            tested += 1;
            if grid(axis).is_some() {
                exact += 1;
            }
        }
    }
    let decimal = tested > 0 && exact * 5 >= tested * 4;
    number(
        &mut out,
        dictionary.len() | if decimal { DECIMAL } else { 0 },
    );
    for brush in dictionary {
        number(&mut out, brush.len());
        out.extend_from_slice(brush);
    }
    number(&mut out, samples.len());
    out.extend_from_slice(&descriptors);
    let start = out.len();
    let first_lane = if decimal { 16 } else { 0 };
    out.resize(start + (SAMPLE - first_lane) * count, 0);
    let mut index = 0;
    for points in samples {
        let mut previous = [0; SAMPLE];
        let mut axes = [0i64; 2];
        for point in points.chunks_exact(SAMPLE) {
            if decimal {
                encode_axis(&mut out, &point[..8], &mut axes[0]);
                encode_axis(&mut out, &point[8..16], &mut axes[1]);
            }
            for lane in first_lane..SAMPLE {
                out[start + (lane - first_lane) * count + index] = point[lane] ^ previous[lane];
                previous[lane] = point[lane];
            }
            index += 1;
        }
    }
    Ok(out)
}

fn unpack(packed: &[u8], raw: usize) -> Result<Vec<u8>, Status> {
    if raw > BLOCK || packed.len() > 2 * BLOCK {
        return Err(Status::Invalid);
    }
    let mut input = Reader(packed);
    let mut brushes = Vec::new();
    let format = input.number()?;
    let decimal = format & DECIMAL != 0;
    for _ in 0..(format & !DECIMAL) {
        let n = input.number()?;
        let brush = input.take(n)?;
        if prefix_len(brush) != Some(n) {
            return Err(Status::Invalid);
        }
        brushes.push(brush);
    }
    let mut entries = Vec::new();
    let mut total = 0;
    let mut raw_size = 0;
    for _ in 0..input.number()? {
        let id = input.take(8)?;
        let n = input.number()?;
        let brush = *brushes.get(input.number()?).ok_or(Status::Invalid)?;
        let n_tail = input.number()?;
        let tail = input.take(n_tail)?;
        if n == 0 || n > raw / SAMPLE {
            return Err(Status::Invalid);
        }
        raw_size += 16 + n * SAMPLE + brush.len() + tail.len();
        if raw_size > raw {
            return Err(Status::Invalid);
        }
        total += n;
        entries.push((id, n, brush, tail));
    }
    if raw_size != raw {
        return Err(Status::Invalid);
    }
    let first_lane = if decimal { 16 } else { 0 };
    let planes = input.take(total * (SAMPLE - first_lane))?;
    let mut out = Vec::with_capacity(raw);
    let mut index = 0;
    for (id, n, brush, tail) in entries {
        out.extend_from_slice(id);
        number(&mut out, n);
        number(&mut out, brush.len() + tail.len());
        let mut previous = [0; SAMPLE];
        let mut axes = [0i64; 2];
        for _ in 0..n {
            if decimal {
                previous[..8].copy_from_slice(&decode_axis(&mut input, &mut axes[0])?);
                previous[8..16].copy_from_slice(&decode_axis(&mut input, &mut axes[1])?);
            }
            for (lane, value) in previous[first_lane..].iter_mut().enumerate() {
                *value ^= planes[lane * total + index];
            }
            out.extend_from_slice(&previous);
            index += 1;
        }
        out.extend_from_slice(brush);
        out.extend_from_slice(tail);
    }
    if !input.0.is_empty() {
        return Err(Status::Invalid);
    }
    Ok(out)
}

/// # Safety
/// All buffers must be live and disjoint, as specified by the core ABI.
#[no_mangle]
pub unsafe extern "C" fn fc_document_pack(
    src: *const u8,
    n: usize,
    dest: *mut u8,
    cap: usize,
    size: *mut usize,
) -> i32 {
    status(|| output(&pack(input(src, n)?)?, dest, cap, size))
}

/// # Safety
/// src and dest must be disjoint and valid for their specified lengths.
#[no_mangle]
pub unsafe extern "C" fn fc_document_unpack(
    src: *const u8,
    n: usize,
    dest: *mut u8,
    raw: usize,
) -> i32 {
    status(|| {
        let mut size = 0;
        output(&unpack(input(src, n)?, raw)?, dest, raw, &mut size)
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn decimal_samples_and_escaped_bits_are_lossless() {
        let mut raw = Vec::new();
        for id in 1..=3_u64 {
            raw.extend(id.to_le_bytes());
            number(&mut raw, 140);
            number(&mut raw, 27);
            for i in 0..140 {
                let x = match i {
                    109 => -0.0,
                    117 => 1.0 / 3.0,
                    _ => (300 - i) as f64 / 100.0,
                };
                raw.extend(x.to_le_bytes());
                raw.extend((i as f64 / -100.0).to_le_bytes());
                raw.extend((i as f32 / 141.0).to_le_bytes());
            }
            let mut brush = [0; 27];
            brush[0] = 1;
            raw.extend(brush);
        }
        let packed = pack(&raw).unwrap();
        assert_eq!(
            u32::from_le_bytes(packed[..4].try_into().unwrap()),
            0x8000_0001
        );
        assert_eq!(unpack(&packed, raw.len()).unwrap(), raw);
        assert!(unpack(&packed[..packed.len() - 1], raw.len()).is_err());
        assert!(unpack(&packed, raw.len() - 1).is_err());
    }
}
