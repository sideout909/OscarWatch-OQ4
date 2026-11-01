#ifndef OSCARWATCH_FT8_H
#define OSCARWATCH_FT8_H

#include <stdint.h>

#ifdef _WIN32
#  ifdef OSCARWATCH_FT8_EXPORTS
#    define OW_FT8_API __declspec(dllexport)
#  else
#    define OW_FT8_API __declspec(dllimport)
#  endif
#else
#  define OW_FT8_API __attribute__((visibility("default")))
#endif

#ifdef __cplusplus
extern "C" {
#endif

#define OW_FT8_MAX_MESSAGE_LEN 48
#define OW_FT8_MAX_DECODES 50

/// Pack an FT4-format message and return its 174 FT4 LDPC codeword bits (one bit per byte).
OW_FT8_API int ow_oq4_encode_bits(const char* message_text, uint8_t* bits, int capacity);

/// Decode OQ4 codeword log-likelihoods (positive means bit 1), requiring LDPC and CRC success.
OW_FT8_API int ow_oq4_soft_bits(const float* likelihoods, float* extrinsic);
OW_FT8_API int ow_oq4_decode_bits(const float* likelihoods, char* text, int capacity);

typedef struct
{
    float freq_hz;
    float time_sec;
    float snr;
    char text[OW_FT8_MAX_MESSAGE_LEN];
    /// Non-zero when the text is a hinted reply: 2 when the calls were supplied and
    /// the rest passed the CRC, 1 when it is only the closest message in the hint list.
    int ap;
    /// Linear slide (Hz/s) the drift search matched; 0 for a steady signal.
    float drift_hz_s;
} ow_ft8_decode_t;

/// Encode a plain FT4/FT8 message text to a 12 kHz float PCM buffer (slot-length with silence padding).
/// @param message_text null-terminated message (e.g. "CQ MM9SQL IO85")
/// @param freq_hz tone base frequency (audio Hz)
/// @param is_ft4 non-zero for FT4, zero for FT8
/// @param out_samples caller-provided buffer; capacity must be at least sample_rate * slot_seconds
/// @param out_capacity number of floats in out_samples
/// @param sample_rate typically 12000
/// @param out_count written sample count
/// @return 0 on success, negative on error
OW_FT8_API int ow_ft8_encode_pcm(
    const char* message_text,
    float freq_hz,
    int is_ft4,
    float* out_samples,
    int out_capacity,
    int sample_rate,
    int* out_count);

/// Same as ow_ft8_encode_pcm, with every tone moving linearly by slope_hz_s
/// (zero offset at the slot start, sample 0) and the waveform scaled by gain.
/// Use the playback rate directly as sample_rate to skip resampling.
OW_FT8_API int ow_ft8_encode_pcm_ex(
    const char* message_text,
    float freq_hz,
    int is_ft4,
    float slope_hz_s,
    float gain,
    float* out_samples,
    int out_capacity,
    int sample_rate,
    int* out_count);

/// Decode one slot of float PCM at the given sample rate.
/// @param samples mono float PCM for one slot (FT4 ≈ 7.5 s, FT8 ≈ 15 s)
/// @param num_samples length of samples
/// @param sample_rate sample rate in Hz
/// @param is_ft4 non-zero for FT4
/// @param f_min_hz lower audio search bound (Hz); invalid ranges fall back to 200–2800
/// @param f_max_hz upper audio search bound (Hz)
/// @param out_decodes output array
/// @param out_capacity max entries in out_decodes
/// @param deep non-zero for the horizon budget (more candidates, longer LDPC)
/// @return number of decoded messages, or negative on error
OW_FT8_API int ow_ft8_decode_pcm(
    const float* samples,
    int num_samples,
    int sample_rate,
    int is_ft4,
    float f_min_hz,
    float f_max_hz,
    ow_ft8_decode_t* out_decodes,
    int out_capacity,
    int deep);

/// Decode one slot, then try newline-separated hint messages on candidates the
/// CRC decode missed. Hints are the report, RRR, RR73 and 73 for a contact
/// whose calls are already known. hint_half_hz <= 0 searches every candidate.
/// @return number of decoded messages, or negative on error
OW_FT8_API int ow_ft8_decode_pcm_ap(
    const float* samples,
    int num_samples,
    int sample_rate,
    int is_ft4,
    float f_min_hz,
    float f_max_hz,
    ow_ft8_decode_t* out_decodes,
    int out_capacity,
    int deep,
    const char* hints_nl,
    float hint_hz,
    float hint_half_hz);

/// Same as ow_ft8_decode_pcm_ap, and also searches FT4 signals whose tones
/// drift linearly by up to max_residual_hz_per_sec either way, in drift_steps
/// steps each side. The reported frequency is the middle of the burst.
/// dt_min_sec and dt_max_sec limit the start-time search; equal values use the
/// full default window.
/// @return number of decoded messages, or negative on error
OW_FT8_API int ow_ft8_decode_pcm_drift(
    const float* samples,
    int num_samples,
    int sample_rate,
    int is_ft4,
    float f_min_hz,
    float f_max_hz,
    ow_ft8_decode_t* out_decodes,
    int out_capacity,
    int deep,
    const char* hints_nl,
    float hint_hz,
    float hint_half_hz,
    float max_residual_hz_per_sec,
    int drift_steps,
    float dt_min_sec,
    float dt_max_sec);

/// Remember a callsign for hash-table resolution during later decodes.
OW_FT8_API void ow_ft8_remember_callsign(const char* callsign);

/// Clear the callsign hash table.
OW_FT8_API void ow_ft8_clear_callsigns(void);

#ifdef __cplusplus
}
#endif

#endif /* OSCARWATCH_FT8_H */
