#include "oscarwatch_ft8.h"
#include "ow_profile.h"

#include <math.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#ifdef _WIN32
#include <windows.h>
#else
#include <pthread.h>
#endif

#include <common/common.h>
#include <common/monitor.h>
#include <ft8/constants.h>
#include <ft8/decode.h>
#include <ft8/encode.h>
#include <ft8/ldpc.h>
#include <ft8/message.h>

#define FT8_SYMBOL_BT 2.0f
#define FT4_SYMBOL_BT 1.0f
#define GFSK_CONST_K 5.336446f

#define CALLSIGN_HASHTABLE_SIZE 256
#define kMin_score 10
#define kMin_score_deep 6
#define kMax_candidates 60
#define kMax_candidates_deep 120
#define kLDPC_iterations 25
#define kLDPC_iterations_deep 40
#define kFreq_osr 2
#define kTime_osr 2

static struct
{
    char callsign[12];
    uint32_t hash;
} callsign_hashtable[CALLSIGN_HASHTABLE_SIZE];

static int callsign_hashtable_size;

#ifdef OW_FT8_PROFILE
double g_ow_prof[OW_PROF_COUNT];
#endif

#ifdef _WIN32
typedef struct
{
    CRITICAL_SECTION cs;
    INIT_ONCE once;
} ow_lock_t;
#define OW_LOCK_INIT { { 0 }, INIT_ONCE_STATIC_INIT }

static BOOL CALLBACK ow_lock_init_once(PINIT_ONCE once, PVOID param, PVOID* context)
{
    (void)once;
    (void)context;
    InitializeCriticalSection((CRITICAL_SECTION*)param);
    return TRUE;
}

static void ow_lock(ow_lock_t* l)
{
    InitOnceExecuteOnce(&l->once, ow_lock_init_once, &l->cs, NULL);
    EnterCriticalSection(&l->cs);
}
static void ow_unlock(ow_lock_t* l)
{
    LeaveCriticalSection(&l->cs);
}
#else
typedef pthread_mutex_t ow_lock_t;
#define OW_LOCK_INIT PTHREAD_MUTEX_INITIALIZER
static void ow_lock(ow_lock_t* l)
{
    pthread_mutex_lock(l);
}
static void ow_unlock(ow_lock_t* l)
{
    pthread_mutex_unlock(l);
}
#endif

static ow_lock_t g_hash_lock = OW_LOCK_INIT;
static void hash_lock(void)
{
    ow_lock(&g_hash_lock);
}
static void hash_unlock(void)
{
    ow_unlock(&g_hash_lock);
}

static void hashtable_init(void)
{
    callsign_hashtable_size = 0;
    memset(callsign_hashtable, 0, sizeof(callsign_hashtable));
}

static void hashtable_add(const char* callsign, uint32_t hash)
{
    hash_lock();
    if (callsign_hashtable_size >= CALLSIGN_HASHTABLE_SIZE - 1)
    {
        hash_unlock();
        return;
    }
    uint16_t hash10 = (hash >> 12) & 0x3FFu;
    int idx_hash = (hash10 * 23) % CALLSIGN_HASHTABLE_SIZE;
    int probes = 0;
    while (callsign_hashtable[idx_hash].callsign[0] != '\0')
    {
        if (((callsign_hashtable[idx_hash].hash & 0x3FFFFFu) == hash)
            && (0 == strcmp(callsign_hashtable[idx_hash].callsign, callsign)))
        {
            callsign_hashtable[idx_hash].hash &= 0x3FFFFFu;
            hash_unlock();
            return;
        }
        idx_hash = (idx_hash + 1) % CALLSIGN_HASHTABLE_SIZE;
        if (++probes >= CALLSIGN_HASHTABLE_SIZE)
        {
            hash_unlock();
            return;
        }
    }
    callsign_hashtable_size++;
    strncpy(callsign_hashtable[idx_hash].callsign, callsign, 11);
    callsign_hashtable[idx_hash].callsign[11] = '\0';
    callsign_hashtable[idx_hash].hash = hash;
    hash_unlock();
}

static bool hashtable_lookup(ftx_callsign_hash_type_t hash_type, uint32_t hash, char* callsign)
{
    hash_lock();
    uint8_t hash_shift = (hash_type == FTX_CALLSIGN_HASH_10_BITS) ? 12
        : (hash_type == FTX_CALLSIGN_HASH_12_BITS ? 10 : 0);
    uint16_t hash10 = (hash >> (12 - hash_shift)) & 0x3FFu;
    int idx_hash = (hash10 * 23) % CALLSIGN_HASHTABLE_SIZE;
    int probes = 0;
    while (callsign_hashtable[idx_hash].callsign[0] != '\0')
    {
        if (((callsign_hashtable[idx_hash].hash & 0x3FFFFFu) >> hash_shift) == hash)
        {
            strcpy(callsign, callsign_hashtable[idx_hash].callsign);
            hash_unlock();
            return true;
        }
        idx_hash = (idx_hash + 1) % CALLSIGN_HASHTABLE_SIZE;
        if (++probes >= CALLSIGN_HASHTABLE_SIZE)
            break;
    }
    callsign[0] = '\0';
    hash_unlock();
    return false;
}

static ftx_callsign_hash_interface_t hash_if = {
    .lookup_hash = hashtable_lookup,
    .save_hash = hashtable_add
};

static int hashtable_ready;

static void ensure_hashtable(void)
{
    hash_lock();
    if (!hashtable_ready)
    {
        hashtable_init();
        hashtable_ready = 1;
    }
    hash_unlock();
}

/* Monitors are shared between decode threads rather than kept per thread: the
   caller starts a new thread for most decodes, and a thread-local monitor was
   never freed when its thread ended. Past the pool size, a decode gets a
   private monitor that is freed on release. */
#define kMonitorPoolSize 8

typedef struct
{
    int pooled;
    int in_use;
    int ready;
    int sample_rate;
    int is_ft4;
    float f_min;
    float f_max;
    monitor_t mon;
} ow_monitor_slot_t;

static ow_monitor_slot_t g_mon_pool[kMonitorPoolSize];
static ow_lock_t g_pool_lock = OW_LOCK_INIT;

static int slot_matches(const ow_monitor_slot_t* s, int sample_rate, int is_ft4, float f_min_hz, float f_max_hz)
{
    return s->ready
        && s->sample_rate == sample_rate
        && s->is_ft4 == is_ft4
        && s->f_min == f_min_hz
        && s->f_max == f_max_hz;
}

static void slot_init(ow_monitor_slot_t* s, int sample_rate, int is_ft4, float f_min_hz, float f_max_hz)
{
    if (s->ready)
        monitor_free(&s->mon);
    monitor_config_t mon_cfg = {
        .f_min = f_min_hz,
        .f_max = f_max_hz,
        .sample_rate = sample_rate,
        .time_osr = kTime_osr,
        .freq_osr = kFreq_osr,
        .protocol = is_ft4 ? FTX_PROTOCOL_FT4 : FTX_PROTOCOL_FT8
    };
    monitor_init(&s->mon, &mon_cfg);
    s->sample_rate = sample_rate;
    s->is_ft4 = is_ft4;
    s->f_min = f_min_hz;
    s->f_max = f_max_hz;
    s->ready = 1;
}

static ow_monitor_slot_t* acquire_monitor(int sample_rate, int is_ft4, float f_min_hz, float f_max_hz)
{
    ow_monitor_slot_t* chosen = NULL;
    ow_lock(&g_pool_lock);
    for (int i = 0; i < kMonitorPoolSize && !chosen; ++i)
    {
        if (!g_mon_pool[i].in_use && slot_matches(&g_mon_pool[i], sample_rate, is_ft4, f_min_hz, f_max_hz))
            chosen = &g_mon_pool[i];
    }
    for (int i = 0; i < kMonitorPoolSize && !chosen; ++i)
    {
        if (!g_mon_pool[i].in_use && !g_mon_pool[i].ready)
            chosen = &g_mon_pool[i];
    }
    for (int i = 0; i < kMonitorPoolSize && !chosen; ++i)
    {
        if (!g_mon_pool[i].in_use)
            chosen = &g_mon_pool[i];
    }
    if (chosen)
    {
        chosen->in_use = 1;
        chosen->pooled = 1;
    }
    ow_unlock(&g_pool_lock);

    if (!chosen)
    {
        chosen = (ow_monitor_slot_t*)calloc(1, sizeof(*chosen));
        if (!chosen)
            return NULL;
    }

    if (slot_matches(chosen, sample_rate, is_ft4, f_min_hz, f_max_hz))
        monitor_reset(&chosen->mon);
    else
        slot_init(chosen, sample_rate, is_ft4, f_min_hz, f_max_hz);
    return chosen;
}

static void release_monitor(ow_monitor_slot_t* slot)
{
    if (!slot)
        return;
    if (!slot->pooled)
    {
        if (slot->ready)
            monitor_free(&slot->mon);
        free(slot);
        return;
    }
    ow_lock(&g_pool_lock);
    slot->in_use = 0;
    ow_unlock(&g_pool_lock);
}

static void gfsk_pulse(int n_spsym, float symbol_bt, float* pulse)
{
    for (int i = 0; i < 3 * n_spsym; ++i)
    {
        float t = i / (float)n_spsym - 1.5f;
        float arg1 = GFSK_CONST_K * symbol_bt * (t + 0.5f);
        float arg2 = GFSK_CONST_K * symbol_bt * (t - 0.5f);
        pulse[i] = (erff(arg1) - erff(arg2)) / 2;
    }
}

/* GFSK pulses by samples per symbol (576 for FT4 at 12 kHz, 2304 at 48 kHz).
   Entries are never freed; the table only holds the few rates in use. */
#define kPulseCacheSize 6
static struct
{
    int n_spsym;
    float symbol_bt;
    float* pulse;
} g_pulse_cache[kPulseCacheSize];
static ow_lock_t g_pulse_lock = OW_LOCK_INIT;

/// @return the shared pulse, or NULL when the table is full (caller builds its own).
static const float* cached_pulse(int n_spsym, float symbol_bt)
{
    const float* found = NULL;
    ow_lock(&g_pulse_lock);
    for (int i = 0; i < kPulseCacheSize; ++i)
    {
        if (!g_pulse_cache[i].pulse)
        {
            float* p = (float*)malloc((size_t)(3 * n_spsym) * sizeof(float));
            if (p)
            {
                gfsk_pulse(n_spsym, symbol_bt, p);
                g_pulse_cache[i].n_spsym = n_spsym;
                g_pulse_cache[i].symbol_bt = symbol_bt;
                g_pulse_cache[i].pulse = p;
                found = p;
            }
            break;
        }
        if (g_pulse_cache[i].n_spsym == n_spsym && g_pulse_cache[i].symbol_bt == symbol_bt)
        {
            found = g_pulse_cache[i].pulse;
            break;
        }
    }
    ow_unlock(&g_pulse_lock);
    return found;
}

/// Shared pulse when one is cached, otherwise a private copy in *storage (free it).
static const float* acquire_pulse(int n_spsym, float symbol_bt, float** storage)
{
    *storage = NULL;
    const float* pulse = cached_pulse(n_spsym, symbol_bt);
    if (pulse)
        return pulse;
    *storage = (float*)malloc((size_t)(3 * n_spsym) * sizeof(float));
    if (*storage)
        gfsk_pulse(n_spsym, symbol_bt, *storage);
    return *storage;
}

/// Tones start at f0 and every frequency moves by slope_hz_s, with zero offset
/// t0_samples before the first signal sample (the slot start), so the burst
/// keeps its slot-start audio position like Ft4AudioDoppler.RemoveLinearDrift.
/// @return 0 on success, -1 on allocation failure (signal left untouched).
static int synth_gfsk(
    const uint8_t* symbols,
    int n_sym,
    float f0,
    float slope_hz_s,
    int t0_samples,
    float gain,
    float symbol_bt,
    float symbol_period,
    int signal_rate,
    float* signal)
{
    int n_spsym = (int)(0.5f + signal_rate * symbol_period);
    int n_wave = n_sym * n_spsym;
    float hmod = 1.0f;
    float dphi_peak = 2 * (float)M_PI * hmod / n_spsym;

    float* dphi = (float*)malloc((size_t)(n_wave + 2 * n_spsym) * sizeof(float));
    float* pulse_storage;
    const float* pulse = acquire_pulse(n_spsym, symbol_bt, &pulse_storage);

    if (!dphi || !pulse)
    {
        free(dphi);
        free(pulse_storage);
        return -1;
    }

    /* dphi[k + n_spsym] advances the phase after signal sample k. */
    const double rate = signal_rate;
    for (int i = 0; i < n_wave + 2 * n_spsym; ++i)
    {
        double t = (t0_samples + i - n_spsym) / rate;
        dphi[i] = (float)(2 * M_PI * (f0 + slope_hz_s * t) / rate);
    }

    for (int i = 0; i < n_sym; ++i)
    {
        int ib = i * n_spsym;
        for (int j = 0; j < 3 * n_spsym; ++j)
            dphi[j + ib] += dphi_peak * symbols[i] * pulse[j];
    }

    for (int j = 0; j < 2 * n_spsym; ++j)
    {
        dphi[j] += dphi_peak * pulse[j + n_spsym] * symbols[0];
        dphi[j + n_sym * n_spsym] += dphi_peak * pulse[j] * symbols[n_sym - 1];
    }

    /* Every phase step is under one turn either way (the carrier sits below the
       sample rate; a steep negative slope can take it below 0 Hz), so one
       correction keeps phi in range. */
    const float two_pi = 2 * (float)M_PI;
    float phi = 0;
    for (int k = 0; k < n_wave; ++k)
    {
        signal[k] = gain * sinf(phi);
        phi += dphi[k + n_spsym];
        if (phi >= two_pi)
            phi -= two_pi;
        else if (phi < 0)
            phi += two_pi;
    }

    int n_ramp = n_spsym / 8;
    for (int i = 0; i < n_ramp; ++i)
    {
        float env = (1 - cosf(2 * (float)M_PI * i / (2 * n_ramp))) / 2;
        signal[i] *= env;
        signal[n_wave - 1 - i] *= env;
    }

    free(dphi);
    free(pulse_storage);
    return 0;
}

OW_FT8_API void ow_ft8_remember_callsign(const char* callsign)
{
    ensure_hashtable();
    if (callsign == NULL || callsign[0] == '\0')
        return;
    /* Hash is computed when encoding/decoding; store with a placeholder hash via encode path. */
    ftx_message_t msg;
    char buf[64];
    snprintf(buf, sizeof(buf), "CQ %s", callsign);
    ftx_message_encode(&msg, &hash_if, buf);
}

OW_FT8_API void ow_ft8_clear_callsigns(void)
{
    hash_lock();
    hashtable_init();
    hashtable_ready = 1;
    hash_unlock();
}

OW_FT8_API int ow_oq4_encode_bits(const char* message_text, uint8_t* bits, int capacity)
{
    if (!message_text || !bits || capacity < FTX_LDPC_N)
        return -1;
    ensure_hashtable();
    ftx_message_t msg = {0};
    if (ftx_message_encode(&msg, &hash_if, message_text) != FTX_MESSAGE_RC_OK)
        return -2;
    uint8_t padded[FTX_LDPC_K_BYTES] = {0};
    memcpy(padded, msg.payload, FTX_PAYLOAD_LENGTH_BYTES);
    return ftx_codeword_bits(1, padded, bits);
}

OW_FT8_API int ow_oq4_decode_bits(const float* likelihoods, char* text, int capacity)
{
    if (!likelihoods || !text || capacity < FTX_MAX_MESSAGE_LENGTH) return -1;
    ensure_hashtable();
    ftx_waterfall_t wf = {0};
    wf.protocol = FTX_PROTOCOL_FT4;
    ftx_message_t msg = {0};
    ftx_decode_status_t status = {0};
    if (!ftx_decode_candidate_llr(&wf, likelihoods, 40, &msg, &status)) return 0;
    ftx_message_offsets_t offsets;
    if (ftx_message_decode(&msg, &hash_if, text, &offsets) != FTX_MESSAGE_RC_OK) return 0;
    return 1;
}

OW_FT8_API int ow_ft8_encode_pcm(
    const char* message_text,
    float freq_hz,
    int is_ft4,
    float* out_samples,
    int out_capacity,
    int sample_rate,
    int* out_count)
{
    return ow_ft8_encode_pcm_ex(
        message_text, freq_hz, is_ft4, 0.0f, 1.0f, out_samples, out_capacity, sample_rate, out_count);
}

OW_FT8_API int ow_ft8_encode_pcm_ex(
    const char* message_text,
    float freq_hz,
    int is_ft4,
    float slope_hz_s,
    float gain,
    float* out_samples,
    int out_capacity,
    int sample_rate,
    int* out_count)
{
    ensure_hashtable();
    if (!message_text || !out_samples || !out_count || sample_rate <= 0 || out_capacity <= 0)
        return -1;
    if (!isfinite(freq_hz) || !isfinite(slope_hz_s) || !isfinite(gain))
        return -1;

    ftx_message_t msg;
    ftx_message_rc_t rc = ftx_message_encode(&msg, &hash_if, message_text);
    if (rc != FTX_MESSAGE_RC_OK)
        return -2;

    int num_tones = is_ft4 ? FT4_NN : FT8_NN;
    float symbol_period = is_ft4 ? FT4_SYMBOL_PERIOD : FT8_SYMBOL_PERIOD;
    float symbol_bt = is_ft4 ? FT4_SYMBOL_BT : FT8_SYMBOL_BT;
    float slot_time = is_ft4 ? FT4_SLOT_TIME : FT8_SLOT_TIME;

    uint8_t* tones = (uint8_t*)malloc((size_t)num_tones);
    if (!tones)
        return -3;

    if (is_ft4)
        ft4_encode(msg.payload, tones);
    else
        ft8_encode(msg.payload, tones);

    int num_samples = (int)(0.5f + num_tones * symbol_period * sample_rate);
    int slot_samples = (int)(0.5f + slot_time * sample_rate);
    /* FT4: keep a short lead-in so the burst stays early in the slot for
     * receive-side copies; remaining silence follows the waveform. */
    int num_silence_head = (int)(0.5f + 0.5f * sample_rate); /* 0.5 s */
    if (num_silence_head + num_samples > slot_samples)
        num_silence_head = 0;
    int num_silence_tail = slot_samples - num_silence_head - num_samples;
    if (num_silence_tail < 0)
        num_silence_tail = 0;
    int num_total = num_silence_head + num_samples + num_silence_tail;
    if (num_total > out_capacity)
    {
        free(tones);
        return -4;
    }

    memset(out_samples, 0, (size_t)num_total * sizeof(float));
    if (synth_gfsk(
            tones, num_tones, freq_hz, slope_hz_s, num_silence_head, gain,
            symbol_bt, symbol_period, sample_rate, out_samples + num_silence_head) != 0)
    {
        free(tones);
        return -3;
    }
    free(tones);

    *out_count = num_total;
    return 0;
}

/* WSJT-X SNR is the signal power against the noise in 2500 Hz.
   The bandwidth term uses the Hann window's noise bandwidth. kSnrFitDb
   recentres that model: on white noise it read about half a decibel high
   from -15 dB to +10 dB. */
#define kSnrFitDb (-0.6f)
#define kSnrFloorDb (-21.0f)
#define kSnrCeilDb 49.0f

/* k-th smallest of v[0..n-1] (Hoare selection). Reorders v. */
static float select_kth(float* v, int n, int k)
{
    int lo = 0, hi = n - 1;
    while (lo < hi)
    {
        float pivot = v[(lo + hi) / 2];
        int i = lo, j = hi;
        while (i <= j)
        {
            while (v[i] < pivot)
                ++i;
            while (v[j] > pivot)
                --j;
            if (i <= j)
            {
                float t = v[i];
                v[i] = v[j];
                v[j] = t;
                ++i;
                --j;
            }
        }
        if (k <= j)
            hi = j;
        else if (k >= i)
            lo = i;
        else
            break;
    }
    return v[k];
}

static float waterfall_power_at(const ftx_waterfall_t* wf, int offset)
{
    return powf(10.0f, 0.1f * WF_ELEM_MAG(wf->mag[offset]));
}

static float estimate_snr_db(
    const monitor_t* mon,
    const ftx_candidate_t* cand,
    const uint8_t* tones,
    int num_symbols,
    int num_fsk,
    int skip_ends)
{
    const ftx_waterfall_t* wf = &mon->wf;
    enum { kNoiseCap = 2048 };
    float noise[kNoiseCap];

    double sig_sum = 0.0;
    int sig_n = 0;
    int noise_n = 0;
    int first = skip_ends ? 1 : 0;
    int last = num_symbols - (skip_ends ? 1 : 0);

    for (int sym = first; sym < last; ++sym)
    {
        int tone = tones[sym];
        if (tone < 0 || tone >= num_fsk)
            continue;

        int offset, bin;
        if (!ftx_candidate_track(wf, cand, sym, &offset, &bin))
            continue;
        sig_sum += waterfall_power_at(wf, offset + tone);
        ++sig_n;

        /* Bins just outside the tone group, same symbol, so a sloping passband
           does not set the noise from the far end of the waterfall. */
        for (int rel = -12; rel <= num_fsk + 11 && noise_n < kNoiseCap; ++rel)
        {
            if (rel >= -1 && rel <= num_fsk)
                continue;
            if (bin + rel < 0 || bin + rel >= wf->num_bins)
                continue;
            noise[noise_n++] = waterfall_power_at(wf, offset + rel);
        }
    }

    float snr = kSnrFloorDb;
    if (sig_n > 0 && noise_n >= 8)
    {
        float noi = select_kth(noise, noise_n, noise_n / 2);
        float sig = (float)(sig_sum / (double)sig_n);
        if (noi > 0.0f && sig > noi)
        {
            int freq_osr = wf->freq_osr > 0 ? wf->freq_osr : 1;
            float tone_hz = 1.0f / mon->symbol_period;
            /* Hann equivalent noise bandwidth is 1.5 FFT bins. */
            float noise_bw = 1.5f * tone_hz / (float)freq_osr;
            float excess = (sig / noi) - 1.0f;
            snr = 10.0f * log10f(excess) + 10.0f * log10f(noise_bw / 2500.0f) + kSnrFitDb;
        }
    }

    if (snr < kSnrFloorDb)
        snr = kSnrFloorDb;
    if (snr > kSnrCeilDb)
        snr = kSnrCeilDb;
    return snr;
}

#define kApMaxHints 160
/* Soft agreement (matched LLR energy over total LLR energy) a candidate needs
   before it is worth a belief-propagation run with the hint. Only a cost bound:
   that route needs the CRC and a text from the hint list. */
#define kApPrefilterMin 0.25f
/* Agreement accepted without the CRC: in effect the most likely message in the
   hint list. Set above the best score noise produced with this hint list, and
   below a real message at about -18 dB. Tuned in the native round-trip tests. */
#define kApQualityMin 0.40f
#define kApMinBits 150
#define kApMaxAttempts 16
/* Codeword bits 0..57 are the two c28 calls and their flags, the same for every
   hint about one contact. Pinned to the hint; the rest is left to the decoder. */
#define kApKnownBits 58
/* Normalised LLRs have a standard deviation near 5 */
#define kApClampLlr 20.0f

typedef struct
{
    int cand;
    int hint;
    float q;
} ap_attempt_t;

/* Keeps the kApMaxAttempts best by agreement, highest first. */
static void ap_attempt_add(ap_attempt_t* list, int* n, int cand, int hint, float q)
{
    int pos = *n;
    if (pos == kApMaxAttempts)
    {
        if (q <= list[pos - 1].q)
            return;
        --pos;
    }
    else
    {
        ++*n;
    }
    while (pos > 0 && list[pos - 1].q < q)
    {
        list[pos] = list[pos - 1];
        --pos;
    }
    list[pos].cand = cand;
    list[pos].hint = hint;
    list[pos].q = q;
}

typedef struct
{
    char text[OW_FT8_MAX_MESSAGE_LEN];
    uint8_t bits[FTX_LDPC_N];
    uint8_t payload[FTX_PAYLOAD_LENGTH_BYTES];
} ap_hint_t;

static int build_ap_hints(const char* hints_nl, int is_ft4, ap_hint_t* hints, int cap)
{
    int count = 0;
    const char* p = hints_nl;
    while (p && *p && count < cap)
    {
        while (*p == '\n' || *p == '\r')
            ++p;
        if (*p == '\0')
            break;

        const char* eol = p;
        while (*eol && *eol != '\n' && *eol != '\r')
            ++eol;

        int len = (int)(eol - p);
        if (len > 0 && len < OW_FT8_MAX_MESSAGE_LEN)
        {
            char text[OW_FT8_MAX_MESSAGE_LEN];
            memcpy(text, p, (size_t)len);
            text[len] = '\0';

            ftx_message_t msg;
            if (ftx_message_encode(&msg, &hash_if, text) == FTX_MESSAGE_RC_OK
                && ftx_codeword_bits(is_ft4, msg.payload, hints[count].bits) == 0)
            {
                /* Keep the text as a decode prints it (hashed calls in <>), so it matches. */
                char decoded[FTX_MAX_MESSAGE_LENGTH];
                ftx_message_offsets_t offsets;
                if (ftx_message_decode(&msg, &hash_if, decoded, &offsets) == FTX_MESSAGE_RC_OK
                    && strlen(decoded) < OW_FT8_MAX_MESSAGE_LEN)
                    strcpy(hints[count].text, decoded);
                else
                    memcpy(hints[count].text, text, (size_t)len + 1);
                memcpy(hints[count].payload, msg.payload, FTX_PAYLOAD_LENGTH_BYTES);
                ++count;
            }
        }
        p = eol;
    }
    return count;
}

/* 1 when the hypothesized bits agree with the LLRs, weighted by how sure each bit is. */
static float ap_quality(const float* llr, const uint8_t* bits, int* usable)
{
    float agree = 0.0f;
    float energy = 0.0f;
    int n = 0;
    for (int i = 0; i < FTX_LDPC_N; ++i)
    {
        float x = llr[i];
        if (x == 0.0f)
            continue;
        ++n;
        energy += fabsf(x);
        agree += bits[i] ? x : -x;
    }
    *usable = n;
    if (energy < 1.0f)
        return -1.0f;
    return agree / energy;
}

static int text_already_decoded(const ow_ft8_decode_t* decoded, int count, const char* text)
{
    for (int i = 0; i < count; ++i)
    {
        if (strcmp(decoded[i].text, text) == 0)
            return 1;
    }
    return 0;
}

/* One FT4 transmission is about 80 Hz wide. A hinted message inside that
   patch, at the same moment, is the same energy read a second way. */
#define kApBurstHz 100.0f
#define kApBurstSec 0.30f

static int burst_already_decoded(
    const ow_ft8_decode_t* decoded, int count, float freq_hz, float time_sec)
{
    if (!decoded || count <= 0)
        return 0;
    for (int i = 0; i < count; ++i)
    {
        if (fabsf(decoded[i].freq_hz - freq_hz) <= kApBurstHz
            && fabsf(decoded[i].time_sec - time_sec) <= kApBurstSec)
            return 1;
    }
    return 0;
}

/* A strong trace is rebuilt and taken out of the audio so a weaker one on the
   same frequency can be decoded. The carrier phase is fitted; a poor fit is
   left alone so a bad alignment cannot dig a hole in the neighbour. */
#define kSubDelayRadius 1800
#define kSubDelayStep 24
#define kSubFreqRadius 12.0f
#define kSubFreqStep 0.5f
/* Most of the sample energy is noise outside the trace, so a weak but real
   alignment explains only a few thousandths of it. A chance delay stays near
   0.001. Anything above this is the signal we just decoded. */
#define kSubMinExplained 0.004f

typedef struct
{
    float freq_hz;
    float slope_hz_s;
    float time_sec;
    float snr;
    int n_sym;
    uint8_t tones[FT4_NN];
} sub_job_t;

/* Candidate search for one slot. drifts are whole-message sub-bin shifts. */
#define kMaxDriftHypotheses 33
typedef struct
{
    float max_residual_hz_s;
    int drift_steps;
    float dt_min_sec;
    float dt_max_sec;
} ow_search_cfg_t;

static const ow_search_cfg_t kSteadySearch = { 0.0f, 0, 0.0f, 0.0f };

/* Base tone frequency, in the middle of the burst for a drifting candidate. */
static float candidate_freq_hz(const monitor_t* mon, const ftx_candidate_t* cand)
{
    float sub = (float)cand->freq_sub - (cand->drift_floor ? 0.5f : 0.0f);
    return (mon->min_bin + cand->freq_offset + sub / mon->wf.freq_osr) / mon->symbol_period;
}

/* Sub-bins a slope of 1 Hz/s moves the tones over one whole message. */
static float subbins_per_hz_s(const monitor_t* mon, int is_ft4)
{
    int nn = is_ft4 ? FT4_NN : FT8_NN;
    return (float)nn * mon->symbol_period * mon->symbol_period * (float)mon->wf.freq_osr;
}

static void build_search(const monitor_t* mon, int is_ft4, const ow_search_cfg_t* cfg, ftx_search_t* search, int8_t* drifts)
{
    ftx_default_time_window(mon->wf.protocol, &search->time_min, &search->time_max);
    if (cfg->dt_max_sec > cfg->dt_min_sec)
    {
        int tmin = (int)floorf(cfg->dt_min_sec / mon->symbol_period) - 1;
        int tmax = (int)ceilf(cfg->dt_max_sec / mon->symbol_period) + 1;
        if (tmin > search->time_min)
            search->time_min = tmin;
        if (tmax < search->time_max)
            search->time_max = tmax;
        if (search->time_max <= search->time_min)
            ftx_default_time_window(mon->wf.protocol, &search->time_min, &search->time_max);
    }

    drifts[0] = 0;
    int n = 1;
    int steps = cfg->drift_steps;
    if (steps > (kMaxDriftHypotheses - 1) / 2)
        steps = (kMaxDriftHypotheses - 1) / 2;
    float max_sub = cfg->max_residual_hz_s * subbins_per_hz_s(mon, is_ft4);
    if (is_ft4 && steps > 0 && max_sub >= 1.0f)
    {
        if (max_sub > 120.0f)
            max_sub = 120.0f;
        int last = 0;
        for (int i = 1; i <= steps; ++i)
        {
            int d = (int)lrintf(max_sub * (float)i / (float)steps);
            if (d <= last)
                continue;
            drifts[n++] = (int8_t)d;
            drifts[n++] = (int8_t)(-d);
            last = d;
        }
    }
    search->drifts = drifts;
    search->num_drifts = n;
    /* Without drift, neighbouring offsets of one burst are extra decode chances
       and suppressing them loses decodes even in a crowded band. With drift,
       every hypothesis repeats each burst and would fill the budget. */
    search->suppress_neighbours = n > 1;
}

/* Baseband template env*sin(phase) and env*cos(phase). Fixed for a whole
   delay and carrier search, so it is built once per job. */
static int gfsk_baseband(
    const uint8_t* symbols,
    int n_sym,
    float symbol_bt,
    float symbol_period,
    int rate,
    float* bb_s,
    float* bb_c)
{
    int n_spsym = (int)(0.5f + rate * symbol_period);
    int n_wave = n_sym * n_spsym;
    float dphi_peak = 2 * (float)M_PI / n_spsym;

    float* dphi = (float*)calloc((size_t)(n_wave + 2 * n_spsym), sizeof(float));
    float* pulse_storage;
    const float* pulse = acquire_pulse(n_spsym, symbol_bt, &pulse_storage);

    if (!dphi || !pulse)
    {
        free(dphi);
        free(pulse_storage);
        return -1;
    }

    for (int i = 0; i < n_sym; ++i)
    {
        int ib = i * n_spsym;
        for (int j = 0; j < 3 * n_spsym; ++j)
            dphi[j + ib] += dphi_peak * symbols[i] * pulse[j];
    }

    for (int j = 0; j < 2 * n_spsym; ++j)
    {
        dphi[j] += dphi_peak * pulse[j + n_spsym] * symbols[0];
        dphi[j + n_sym * n_spsym] += dphi_peak * pulse[j] * symbols[n_sym - 1];
    }

    const float two_pi = 2 * (float)M_PI;
    float phi = 0.0f;
    for (int k = 0; k < n_wave; ++k)
    {
        bb_s[k] = sinf(phi);
        bb_c[k] = cosf(phi);
        phi += dphi[k + n_spsym];
        if (phi >= two_pi)
            phi -= two_pi;
    }

    int n_ramp = n_spsym / 8;
    for (int i = 0; i < n_ramp; ++i)
    {
        float e = (1.0f - cosf(2 * (float)M_PI * i / (2 * n_ramp))) / 2.0f;
        bb_s[i] *= e;
        bb_c[i] *= e;
        bb_s[n_wave - 1 - i] *= e;
        bb_c[n_wave - 1 - i] *= e;
    }

    free(dphi);
    free(pulse_storage);
    return n_wave;
}

/* Template moved up to the audio carrier, at freq_hz in the middle of the burst
   and drifting slope_hz_s. The rotation depends on the template index only, so
   every delay hypothesis at this carrier shares it. Only every stride-th entry
   is written. */
static void rotate_template(
    const float* bb_s,
    const float* bb_c,
    int n_wave,
    float freq_hz,
    float slope_hz_s,
    int rate,
    int stride,
    float* rs,
    float* rc)
{
    /* theta(m) = w*k + a*(k - mid)^2 with k = m*stride. Successive steps of the
       phase increase by a constant, so two rotations cover it; the exact angle
       is reseeded every 256 steps so the error cannot build up. */
    double w = 2.0 * M_PI * (double)freq_hz / (double)rate;
    double a = M_PI * (double)slope_hz_s / ((double)rate * (double)rate);
    double mid = 0.5 * (double)n_wave;
    double s = (double)stride;
    double turn = 2.0 * a * s * s;
    double turn_re = cos(turn);
    double turn_im = sin(turn);
    double cs = 1.0, sn = 0.0, step_re = 1.0, step_im = 0.0;
    int m = 0;
    for (int k = 0; k < n_wave; k += stride, ++m)
    {
        if ((m & 255) == 0)
        {
            double dk = (double)k - mid;
            double theta = w * (double)k + a * dk * dk;
            double delta = w * s + a * (s * s + 2.0 * s * dk);
            cs = cos(theta);
            sn = sin(theta);
            step_re = cos(delta);
            step_im = sin(delta);
        }

        rs[k] = (float)(bb_s[k] * cs + bb_c[k] * sn);
        rc[k] = (float)(bb_c[k] * cs - bb_s[k] * sn);

        double next_cs = cs * step_re - sn * step_im;
        double next_sn = cs * step_im + sn * step_re;
        cs = next_cs;
        sn = next_sn;
        double next_re = step_re * turn_re - step_im * turn_im;
        double next_im = step_re * turn_im + step_im * turn_re;
        step_re = next_re;
        step_im = next_im;
    }
}

/* Correlation of a rotated template against the audio at this start. stride > 1
   is for the search and needs a template rotated with the same stride. When
   subtract is set (stride 1 only), a*sin + b*cos is removed. */
static float match_and_maybe_subtract(
    float* samples,
    int num_samples,
    const float* rs,
    const float* rc,
    int n_wave,
    int start,
    int stride,
    int subtract)
{
    int k0 = start < 0 ? -start : 0;
    int k1 = num_samples - start < n_wave ? num_samples - start : n_wave;
    if (stride > 1)
        k0 = (k0 + stride - 1) / stride * stride;

    double sii = 0.0, sqq = 0.0, siq = 0.0, six = 0.0, sqx = 0.0, energy = 0.0;
    int used = 0;
    for (int k = k0; k < k1; k += stride)
    {
        float s = rs[k];
        float c = rc[k];
        float x = samples[start + k];
        sii += (double)s * s;
        sqq += (double)c * c;
        siq += (double)s * c;
        six += (double)s * x;
        sqx += (double)c * x;
        energy += (double)x * x;
        ++used;
    }

    double det = sii * sqq - siq * siq;
    if (used < 1000 || !(det > 1e-6) || !(energy > 1e-8))
        return -1.0f;

    double a = (sqq * six - siq * sqx) / det;
    double b = (sii * sqx - siq * six) / det;
    double explained = a * six + b * sqx;
    float frac = (float)(explained / energy);
    if (!subtract || stride != 1 || frac < kSubMinExplained)
        return frac;

    for (int k = k0; k < k1; ++k)
        samples[start + k] -= (float)(a * rs[k] + b * rc[k]);
    return frac;
}

static int subtract_job(float* samples, int num_samples, int rate, int is_ft4, const sub_job_t* job)
{
    float symbol_period = is_ft4 ? FT4_SYMBOL_PERIOD : FT8_SYMBOL_PERIOD;
    float symbol_bt = is_ft4 ? FT4_SYMBOL_BT : FT8_SYMBOL_BT;
    int n_spsym = (int)(0.5f + rate * symbol_period);
    int n_wave = job->n_sym * n_spsym;
    float* buf = (float*)malloc((size_t)n_wave * 4 * sizeof(float));
    if (!buf)
        return 0;
    float* bb_s = buf;
    float* bb_c = buf + n_wave;
    float* rs = buf + 2 * n_wave;
    float* rc = buf + 3 * n_wave;

    int built = gfsk_baseband(job->tones, job->n_sym, symbol_bt, symbol_period, rate, bb_s, bb_c);
    if (built != n_wave)
    {
        free(buf);
        return 0;
    }

    const int stride = 4;
    int origin = (int)(job->time_sec * (float)rate + 0.5f);
    int best_delay = 0;
    float best_frac = -1.0f;
    rotate_template(bb_s, bb_c, n_wave, job->freq_hz, job->slope_hz_s, rate, stride, rs, rc);
    for (int delay = -kSubDelayRadius; delay <= kSubDelayRadius; delay += kSubDelayStep)
    {
        float frac = match_and_maybe_subtract(samples, num_samples, rs, rc, n_wave, origin + delay, stride, 0);
        if (frac > best_frac)
        {
            best_frac = frac;
            best_delay = delay;
        }
    }

    for (int delay = best_delay - kSubDelayStep; delay <= best_delay + kSubDelayStep; delay += 2)
    {
        float frac = match_and_maybe_subtract(samples, num_samples, rs, rc, n_wave, origin + delay, stride, 0);
        if (frac > best_frac)
        {
            best_frac = frac;
            best_delay = delay;
        }
    }

    float best_freq = job->freq_hz;
    for (float df = -kSubFreqRadius; df <= kSubFreqRadius + 0.01f; df += kSubFreqStep)
    {
        rotate_template(bb_s, bb_c, n_wave, job->freq_hz + df, job->slope_hz_s, rate, stride, rs, rc);
        float frac = match_and_maybe_subtract(samples, num_samples, rs, rc, n_wave, origin + best_delay, stride, 0);
        if (frac > best_frac)
        {
            best_frac = frac;
            best_freq = job->freq_hz + df;
        }
    }

    int removed = 0;
    if (best_frac >= kSubMinExplained)
    {
        rotate_template(bb_s, bb_c, n_wave, best_freq, job->slope_hz_s, rate, 1, rs, rc);
        float frac = match_and_maybe_subtract(samples, num_samples, rs, rc, n_wave, origin + best_delay, 1, 1);
        removed = frac >= kSubMinExplained;
    }

    free(buf);
    return removed;
}

static int decode_slot(
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
    const ow_search_cfg_t* search_cfg,
    int do_subtract,
    const ow_ft8_decode_t* occupied,
    int occupied_count)
{
    if (!search_cfg)
        search_cfg = &kSteadySearch;
    ensure_hashtable();
    if (!samples || num_samples <= 0 || sample_rate <= 0 || !out_decodes || out_capacity <= 0)
        return -1;

    if (!(f_max_hz > f_min_hz + 99.0f) || f_min_hz < 50.0f || f_max_hz > 3500.0f)
    {
        f_min_hz = 200.0f;
        f_max_hz = 2800.0f;
    }

    if (out_capacity > OW_FT8_MAX_DECODES)
        out_capacity = OW_FT8_MAX_DECODES;

    ftx_protocol_t protocol = is_ft4 ? FTX_PROTOCOL_FT4 : FTX_PROTOCOL_FT8;
    (void)protocol;
    ow_monitor_slot_t* mon_slot = acquire_monitor(sample_rate, is_ft4, f_min_hz, f_max_hz);
    if (!mon_slot)
        return -1;
    monitor_t* mon = &mon_slot->mon;

    OW_PROF_BEGIN(t_monitor);
    const int block_size = mon->block_size;
    int pos = 0;
    while (pos + block_size <= num_samples)
    {
        monitor_process(mon, samples + pos);
        pos += block_size;
    }
    monitor_flatten(mon);
    OW_PROF_END(OW_PROF_MONITOR, t_monitor);

    int deep_pass = deep != 0;
    int max_candidates = deep_pass ? kMax_candidates_deep : kMax_candidates;
    int min_score = deep_pass ? kMin_score_deep : kMin_score;
    int ldpc_iterations = deep_pass ? kLDPC_iterations_deep : kLDPC_iterations;

    ftx_candidate_t candidate_list[kMax_candidates_deep];
    OW_PROF_BEGIN(t_cand);
    ftx_search_t search;
    int8_t drifts[kMaxDriftHypotheses];
    build_search(mon, is_ft4, search_cfg, &search, drifts);
    int num_candidates = ftx_find_candidates_ex(&mon->wf, max_candidates, candidate_list, min_score, &search);
    float hz_s_per_subbin = 1.0f / subbins_per_hz_s(mon, is_ft4);
    OW_PROF_END(OW_PROF_CANDIDATES, t_cand);
    uint8_t candidate_decoded[kMax_candidates_deep];
    memset(candidate_decoded, 0, (size_t)num_candidates);
    float (*cand_llr)[FTX_LDPC_N] = (float (*)[FTX_LDPC_N])malloc((size_t)(num_candidates > 0 ? num_candidates : 1) * sizeof(*cand_llr));
    if (!cand_llr)
    {
        release_monitor(mon_slot);
        return -1;
    }

    int num_decoded = 0;
    sub_job_t jobs[OW_FT8_MAX_DECODES];
    int n_jobs = 0;
    ftx_message_t decoded[OW_FT8_MAX_DECODES];
    ftx_message_t* decoded_hashtable[OW_FT8_MAX_DECODES];
    for (int i = 0; i < OW_FT8_MAX_DECODES; ++i)
        decoded_hashtable[i] = NULL;

    OW_PROF_BEGIN(t_ldpc);
    for (int idx = 0; idx < num_candidates && num_decoded < out_capacity; ++idx)
    {
        const ftx_candidate_t* cand = &candidate_list[idx];
        float freq_hz = candidate_freq_hz(mon, cand);
        float time_sec = (cand->time_offset + (float)cand->time_sub / mon->wf.time_osr) * mon->symbol_period;

        ftx_message_t message;
        ftx_decode_status_t status;
        /* Belief propagation stops as soon as the parity checks pass, so an easy
           candidate costs a few iterations without a separate short pass. */
        ftx_candidate_llr(&mon->wf, cand, cand_llr[idx]);
        if (!ftx_decode_candidate_llr(&mon->wf, cand_llr[idx], ldpc_iterations, &message, &status))
            continue;

        candidate_decoded[idx] = 1;

        int idx_hash = message.hash % OW_FT8_MAX_DECODES;
        bool found_empty_slot = false;
        bool found_duplicate = false;
        do
        {
            if (decoded_hashtable[idx_hash] == NULL)
                found_empty_slot = true;
            else if ((decoded_hashtable[idx_hash]->hash == message.hash)
                && (0 == memcmp(decoded_hashtable[idx_hash]->payload, message.payload, sizeof(message.payload))))
                found_duplicate = true;
            else
                idx_hash = (idx_hash + 1) % OW_FT8_MAX_DECODES;
        } while (!found_empty_slot && !found_duplicate);

        if (!found_empty_slot)
            continue;

        memcpy(&decoded[idx_hash], &message, sizeof(message));
        decoded_hashtable[idx_hash] = &decoded[idx_hash];

        char text[FTX_MAX_MESSAGE_LENGTH];
        ftx_message_offsets_t offsets;
        ftx_message_rc_t unpack_status = ftx_message_decode(&message, &hash_if, text, &offsets);
        if (unpack_status != FTX_MESSAGE_RC_OK)
            snprintf(text, sizeof(text), "ERR%d", (int)unpack_status);

        uint8_t tones[FT4_NN];
        int nsym;
        int nfsk;
        int skip_ends;
        if (is_ft4)
        {
            ft4_encode(message.payload, tones);
            nsym = FT4_NN;
            nfsk = 4;
            skip_ends = 1; /* first and last symbols are ramps, not full power */
        }
        else
        {
            ft8_encode(message.payload, tones);
            nsym = FT8_NN;
            nfsk = 8;
            skip_ends = 0;
        }

        ow_ft8_decode_t* out = &out_decodes[num_decoded++];
        out->freq_hz = freq_hz;
        out->time_sec = time_sec;
        out->snr = estimate_snr_db(mon, cand, tones, nsym, nfsk, skip_ends);
        strncpy(out->text, text, OW_FT8_MAX_MESSAGE_LEN - 1);
        out->text[OW_FT8_MAX_MESSAGE_LEN - 1] = '\0';
        out->ap = 0;
        out->drift_hz_s = (float)cand->drift * hz_s_per_subbin;

        if (do_subtract && n_jobs < OW_FT8_MAX_DECODES && unpack_status == FTX_MESSAGE_RC_OK)
        {
            sub_job_t* job = &jobs[n_jobs++];
            job->freq_hz = freq_hz;
            job->slope_hz_s = out->drift_hz_s;
            job->time_sec = time_sec;
            job->snr = out->snr;
            job->n_sym = nsym;
            memset(job->tones, 0, sizeof(job->tones));
            memcpy(job->tones, tones, (size_t)nsym);
        }
    }
    OW_PROF_END(OW_PROF_LDPC, t_ldpc);

    /* A priori: both calls are known, so try the report / RR73 / 73 list
       against candidates the CRC decode missed. One winner per slot. */
    OW_PROF_BEGIN(t_ap);
    if (is_ft4 && hints_nl && hints_nl[0] != '\0' && num_decoded < out_capacity)
    {
        ap_hint_t* hints = (ap_hint_t*)malloc((size_t)kApMaxHints * sizeof(ap_hint_t));
        if (hints)
        {
            int hint_count = build_ap_hints(hints_nl, 1, hints, kApMaxHints);
            ap_attempt_t attempts[kApMaxAttempts];
            int n_attempts = 0;

            for (int idx = 0; idx < num_candidates && hint_count > 0; ++idx)
            {
                if (candidate_decoded[idx])
                    continue;

                const ftx_candidate_t* cand = &candidate_list[idx];
                float freq_hz = candidate_freq_hz(mon, cand);
                float cand_time = (cand->time_offset + (float)cand->time_sub / mon->wf.time_osr) * mon->symbol_period;
                if (hint_half_hz > 0.0f && fabsf(freq_hz - hint_hz) > hint_half_hz)
                    continue;
                /* The CRC pass already explained this burst. Do not also
                   accept a hinted message to a different station. */
                if (burst_already_decoded(out_decodes, num_decoded, freq_hz, cand_time)
                    || burst_already_decoded(occupied, occupied_count, freq_hz, cand_time))
                    continue;

                const float* llr = cand_llr[idx];

                float local_best = -1.0f;
                int local_hint = -1;
                for (int h = 0; h < hint_count; ++h)
                {
                    int usable = 0;
                    float q = ap_quality(llr, hints[h].bits, &usable);
                    if (usable < kApMinBits)
                        continue;
                    if (q > local_best)
                    {
                        local_best = q;
                        local_hint = h;
                    }
                }

                if (local_hint < 0 || local_best < kApPrefilterMin)
                    continue;
                ap_attempt_add(attempts, &n_attempts, idx, local_hint, local_best);
            }

            /* Best agreement first. The calls are pinned to the hint; the report,
               CRC and parity still have to come out of the audio. */
            int accepted = 0;
            for (int a = 0; a < n_attempts && !accepted; ++a)
            {
                const ftx_candidate_t* cand = &candidate_list[attempts[a].cand];
                const ap_hint_t* hint = &hints[attempts[a].hint];
                float llr[FTX_LDPC_N];
                memcpy(llr, cand_llr[attempts[a].cand], sizeof(llr));
                for (int i = 0; i < kApKnownBits; ++i)
                    llr[i] = hint->bits[i] ? kApClampLlr : -kApClampLlr;

                ftx_message_t message;
                ftx_decode_status_t status;
                if (!ftx_decode_candidate_llr(&mon->wf, llr, ldpc_iterations, &message, &status))
                    continue;
                char text[FTX_MAX_MESSAGE_LENGTH];
                ftx_message_offsets_t offsets;
                if (ftx_message_decode(&message, &hash_if, text, &offsets) != FTX_MESSAGE_RC_OK)
                    continue;
                int match = -1;
                for (int h = 0; h < hint_count && match < 0; ++h)
                {
                    if (strcmp(hints[h].text, text) == 0)
                        match = h;
                }
                if (match < 0 || text_already_decoded(out_decodes, num_decoded, text))
                    continue;

                uint8_t tones[FT4_NN];
                ft4_encode(message.payload, tones);

                ow_ft8_decode_t* out = &out_decodes[num_decoded++];
                out->freq_hz = candidate_freq_hz(mon, cand);
                out->time_sec = (cand->time_offset + (float)cand->time_sub / mon->wf.time_osr) * mon->symbol_period;
                out->snr = estimate_snr_db(mon, cand, tones, FT4_NN, 4, 1);
                strncpy(out->text, hints[match].text, OW_FT8_MAX_MESSAGE_LEN - 1);
                out->text[OW_FT8_MAX_MESSAGE_LEN - 1] = '\0';
                out->ap = 2;
                out->drift_hz_s = (float)cand->drift * hz_s_per_subbin;
                accepted = 1;
            }

            /* Pinning the calls leaves too many bits for the decoder on a weak
               signal, so the most likely hint is still taken on agreement alone.
               Steady candidates only: across drift hypotheses the best of many
               guesses would turn noise into reports. */
            for (int a = 0; a < n_attempts && !accepted; ++a)
            {
                const ftx_candidate_t* cand = &candidate_list[attempts[a].cand];
                const ap_hint_t* hint = &hints[attempts[a].hint];
                if (attempts[a].q < kApQualityMin)
                    break;
                if (cand->drift != 0)
                    continue;
                if (text_already_decoded(out_decodes, num_decoded, hint->text))
                    break;

                uint8_t tones[FT4_NN];
                ft4_encode(hint->payload, tones);
                /* The SNR floor means the hint explains no more energy than the
                   noise around it. Accepting it invents a report. */
                float hinted_snr = estimate_snr_db(mon, cand, tones, FT4_NN, 4, 1);
                if (hinted_snr <= kSnrFloorDb)
                    continue;

                ow_ft8_decode_t* out = &out_decodes[num_decoded++];
                out->freq_hz = candidate_freq_hz(mon, cand);
                out->time_sec = (cand->time_offset + (float)cand->time_sub / mon->wf.time_osr) * mon->symbol_period;
                out->snr = hinted_snr;
                strncpy(out->text, hint->text, OW_FT8_MAX_MESSAGE_LEN - 1);
                out->text[OW_FT8_MAX_MESSAGE_LEN - 1] = '\0';
                out->ap = 1;
                out->drift_hz_s = 0.0f;
                accepted = 1;
            }
            free(hints);
        }
    }
    OW_PROF_END(OW_PROF_AP, t_ap);

    /* The waterfall is not needed past here; the residual pass takes its own. */
    free(cand_llr);
    release_monitor(mon_slot);
    mon_slot = NULL;
    mon = NULL;

    /* One extra pass after the strong traces are removed. A single isolated
       signal still costs the search, and is left in place when the fit is poor. */
    if (do_subtract && n_jobs > 0 && num_decoded < out_capacity)
    {
        float* residual = (float*)malloc((size_t)num_samples * sizeof(float));
        if (residual)
        {
            memcpy(residual, samples, (size_t)num_samples * sizeof(float));
            int removed = 0;
            int used[OW_FT8_MAX_DECODES];
            memset(used, 0, sizeof(used));
            OW_PROF_BEGIN(t_sub);
            for (int n = 0; n < n_jobs; ++n)
            {
                int best = -1;
                for (int j = 0; j < n_jobs; ++j)
                {
                    if (used[j])
                        continue;
                    if (best < 0 || jobs[j].snr > jobs[best].snr)
                        best = j;
                }
                if (best < 0)
                    break;
                used[best] = 1;
                removed += subtract_job(residual, num_samples, sample_rate, is_ft4, &jobs[best]);
            }
            OW_PROF_END(OW_PROF_SUBTRACT, t_sub);

            if (removed > 0)
            {
                ow_ft8_decode_t extra[OW_FT8_MAX_DECODES];
                OW_PROF_BEGIN(t_rec);
                int n_extra = decode_slot(
                    residual, num_samples, sample_rate, is_ft4, f_min_hz, f_max_hz,
                    extra, OW_FT8_MAX_DECODES, deep, hints_nl, hint_hz, hint_half_hz, search_cfg, 0,
                    out_decodes, num_decoded);
                OW_PROF_END(OW_PROF_RECURSE, t_rec);
                for (int i = 0; i < n_extra && num_decoded < out_capacity; ++i)
                {
                    if (text_already_decoded(out_decodes, num_decoded, extra[i].text))
                        continue;
                    out_decodes[num_decoded++] = extra[i];
                }
            }
            free(residual);
        }
    }

    return num_decoded;
}

OW_FT8_API int ow_ft8_decode_pcm(
    const float* samples,
    int num_samples,
    int sample_rate,
    int is_ft4,
    float f_min_hz,
    float f_max_hz,
    ow_ft8_decode_t* out_decodes,
    int out_capacity,
    int deep)
{
    return decode_slot(samples, num_samples, sample_rate, is_ft4, f_min_hz, f_max_hz,
        out_decodes, out_capacity, deep, NULL, 0.0f, 0.0f, NULL, 1, NULL, 0);
}

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
    float hint_half_hz)
{
    return decode_slot(samples, num_samples, sample_rate, is_ft4, f_min_hz, f_max_hz,
        out_decodes, out_capacity, deep, hints_nl, hint_hz, hint_half_hz, NULL, 1, NULL, 0);
}

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
    float dt_max_sec)
{
    ow_search_cfg_t cfg;
    cfg.max_residual_hz_s = isfinite(max_residual_hz_per_sec) ? fabsf(max_residual_hz_per_sec) : 0.0f;
    cfg.drift_steps = drift_steps > 0 ? drift_steps : 0;
    cfg.dt_min_sec = isfinite(dt_min_sec) ? dt_min_sec : 0.0f;
    cfg.dt_max_sec = isfinite(dt_max_sec) ? dt_max_sec : 0.0f;
    return decode_slot(samples, num_samples, sample_rate, is_ft4, f_min_hz, f_max_hz,
        out_decodes, out_capacity, deep, hints_nl, hint_hz, hint_half_hz, &cfg, 1, NULL, 0);
}

// Optional receiver-only API. Existing transmitters and native decoders remain compatible.
OW_FT8_API int ow_oq4_soft_bits(const float* likelihoods, float* extrinsic)
{
    if (!likelihoods || !extrinsic) return -1;
    for (int n = 0; n < 174; ++n) if (!isfinite(likelihoods[n])) return -1;
    int errors;
    bp_decode_soft(likelihoods, 12, extrinsic, &errors);
    return errors;
}
