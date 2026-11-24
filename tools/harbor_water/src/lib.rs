// SPDX-License-Identifier: BSD-2-Clause
/// Advances a damped height field into the previous frame buffer.
/// # Safety
/// Both pointers must cover separate initialized arrays of size * size floats.
#[no_mangle]
pub unsafe extern "C" fn guo_water_step_v1(
    now: *const f32, previous: *mut f32, size: i32, speed2: f32, damping: f32,
) -> i32 {
    if now.is_null() || previous.is_null() || !(3..=4096).contains(&size) {
        return 0;
    }
    let width = size as usize;
    let current = std::slice::from_raw_parts(now, width * width);
    let before = std::slice::from_raw_parts_mut(previous, width * width);
    for y in 1..width - 1 {
        for x in 1..width - 1 {
            let i = y * width + x;
            let c = current[i];
            let neighbors = current[i - 1] + current[i + 1] + current[i - width] + current[i + width];
            before[i] = (2.0 * c - before[i] + speed2 * (neighbors - 4.0 * c)) * damping;
        }
    }
    1
}
