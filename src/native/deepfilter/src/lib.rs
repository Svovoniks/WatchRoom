use std::panic::{catch_unwind, AssertUnwindSafe};

use df::tract::{DfParams, DfTract, ReduceMask, RuntimeParams};
use ndarray::{ArrayView2, ArrayViewMut2};

pub struct Denoiser(DfTract);

/// Creates a mono 48 kHz DeepFilterNet 3 denoiser; null if the model fails to load.
#[no_mangle]
pub extern "C" fn wr_df_create(atten_lim_db: f32) -> *mut Denoiser {
    catch_unwind(|| {
        let params = RuntimeParams::default_with_ch(1)
            .with_atten_lim(atten_lim_db)
            .with_thresholds(-15.0, 35.0, 35.0)
            .with_post_filter(0.0)
            .with_mask_reduce(ReduceMask::MAX);
        DfTract::new(DfParams::default(), &params).ok().map(|m| Box::into_raw(Box::new(Denoiser(m))))
    })
    .ok()
    .flatten()
    .unwrap_or(std::ptr::null_mut())
}

/// Samples per call to wr_df_process.
#[no_mangle]
pub unsafe extern "C" fn wr_df_frame_length(st: *const Denoiser) -> usize {
    st.as_ref().map_or(0, |s| s.0.hop_size)
}

/// Algorithmic delay in samples between input and output.
#[no_mangle]
pub unsafe extern "C" fn wr_df_delay(st: *const Denoiser) -> usize {
    st.as_ref().map_or(0, |s| s.0.fft_size - s.0.hop_size + s.0.lookahead * s.0.hop_size)
}

#[no_mangle]
pub unsafe extern "C" fn wr_df_set_atten_lim(st: *mut Denoiser, atten_lim_db: f32) {
    if let Some(s) = st.as_mut() {
        s.0.set_atten_lim(atten_lim_db);
    }
}

/// Denoises one frame of wr_df_frame_length samples in [-1, 1]. Returns 0 on success, -1 on failure.
#[no_mangle]
pub unsafe extern "C" fn wr_df_process(st: *mut Denoiser, input: *const f32, output: *mut f32) -> i32 {
    let Some(s) = st.as_mut() else { return -1 };
    if input.is_null() || output.is_null() {
        return -1;
    }
    let hop = s.0.hop_size;
    let result = catch_unwind(AssertUnwindSafe(|| {
        let noisy = ArrayView2::from_shape_ptr((1, hop), input);
        let enhanced = ArrayViewMut2::from_shape_ptr((1, hop), output);
        s.0.process(noisy, enhanced)
    }));
    match result {
        Ok(Ok(_)) => 0,
        _ => -1,
    }
}

#[no_mangle]
pub unsafe extern "C" fn wr_df_free(st: *mut Denoiser) {
    if !st.is_null() {
        drop(Box::from_raw(st));
    }
}
