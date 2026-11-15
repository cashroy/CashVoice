// CashVoice DSP — real-time safe (no allocation in process()).
#pragma once
#include <algorithm>
#include <cmath>
#include <cstdint>
#include <vector>

namespace vf {

struct Params {
    bool  voiceOn = false;
    bool  micMute = false;
    bool  soundOn = true;
    float pitch = 0, formant = 0;            // semitones
    float ringHz = 50, ringMix = 0;
    float echoMs = 250, echoFb = 0.4f, echoMix = 0;
    float hpHz = 0, lpHz = 0, drive = 0;
    float micGain = 1, soundGain = 1;
};

constexpr float kPi = 3.14159265359f;
constexpr float kTwoPi = 6.28318530718f;

static inline float Flush(float v) { return std::fabs(v) < 1e-20f ? 0.f : v; }

// ---- 2nd-order (Butterworth) filter, RBJ cookbook, transposed direct form II ----------------
struct Biquad {
    float b0 = 1, b1 = 0, b2 = 0, a1 = 0, a2 = 0, z1 = 0, z2 = 0;
    void setLowPass(float hz, float sr)  { set(hz, sr, false); }
    void setHighPass(float hz, float sr) { set(hz, sr, true); }
    void set(float hz, float sr, bool high) {
        hz = std::min(std::max(hz, 10.f), 0.45f * sr);
        float w0 = kTwoPi * hz / sr, c = std::cos(w0), al = std::sin(w0) / (2.f * 0.70710678f);
        float a0 = 1.f + al;
        if (high) { b0 = (1.f + c) * 0.5f; b1 = -(1.f + c); }
        else      { b0 = (1.f - c) * 0.5f; b1 = 1.f - c; }
        b2 = b0;
        b0 /= a0; b1 /= a0; b2 /= a0;
        a1 = -2.f * c / a0; a2 = (1.f - al) / a0;
    }
    inline float process(float x) {
        float y = b0 * x + z1;
        z1 = Flush(b1 * x - a1 * y + z2);
        z2 = Flush(b2 * x - a2 * y);
        return y;
    }
};

// ---- Pitch + formant shifter: TD-PSOLA ----------------------------------------------------
// Finds the voice's pitch period (YIN on a 4x-decimated copy), then rebuilds the signal from
// two-period, Hann-windowed grains taken at that period and laid down at period / pitchRatio.
// Copying whole periods keeps the vocal-tract resonances (formants) where they were, so the
// voice stays clear instead of sounding like a sped-up tape. Reading each grain faster or
// slower moves the formants on their own (formant shift). Fixed latency: ~3 longest periods.
struct Psola {
    int    sr = 48000, maxP = 565, minP = 80, D = 1760, mask = 0, dmask = 0;
    std::vector<float> in, acc, wsum, dec, diff;
    int64_t t = 0, ts = 0, dcount = 0;
    double  am = 0;
    float   curT = 264.f, decAcc = 0.f;
    int     decPhase = 0;
    bool    active = false;

    void init(int sampleRate) {
        sr = sampleRate;
        maxP = std::max(64, sr / 85);              // lowest pitch followed: 85 Hz
        minP = std::max(16, sr / 600);             // highest: 600 Hz
        D = 3 * maxP + 64;                         // room for a grain read at 2x formant
        int n = 1; while (n < 4 * D) n <<= 1;
        in.assign((size_t)n, 0.f); acc.assign((size_t)n, 0.f); wsum.assign((size_t)n, 0.f);
        mask = n - 1;
        int d = 1; while (d < (maxP / 4) * 4 + 64) d <<= 1;
        dec.assign((size_t)d, 0.f); dmask = d - 1;
        diff.assign((size_t)(maxP / 4 + 2), 0.f);
        t = ts = dcount = 0; am = 0; curT = 0.0055f * (float)sr; decAcc = 0; decPhase = 0; active = false;
    }

    inline float at(int64_t i) const { return in[(size_t)(i & mask)]; }
    inline float read(double pos) const {          // 4-point Hermite interpolation
        int64_t i = (int64_t)std::floor(pos);
        float fr = (float)(pos - (double)i);
        float xm1 = at(i - 1), x0 = at(i), x1 = at(i + 1), x2 = at(i + 2);
        float c1 = 0.5f * (x1 - xm1);
        float c2 = xm1 - 2.5f * x0 + 2.f * x1 - 0.5f * x2;
        float c3 = 0.5f * (x2 - xm1) + 1.5f * (x0 - x1);
        return ((c3 * fr + c2) * fr + c1) * fr + x0;
    }

    void detect() {
        const int maxL = maxP / 4, minL = std::max(2, minP / 4), W = 2 * maxL;
        if (dcount < (int64_t)(W + maxL + 1)) return;
        const int64_t s = dcount - W - maxL;
        float energy = 0.f;
        for (int i = 0; i < W; ++i) { float v = dec[(size_t)((s + i) & dmask)]; energy += v * v; }
        if (energy < 1e-6f * (float)W) { curT = 0.0055f * (float)sr; return; }   // silence
        float run = 0.f, best = 1e9f; int bestL = -1;
        diff[0] = 1.f;
        for (int L = 1; L <= maxL; ++L) {
            float d = 0.f;
            for (int i = 0; i < W; ++i) {
                float a = dec[(size_t)((s + i) & dmask)] - dec[(size_t)((s + i + L) & dmask)];
                d += a * a;
            }
            run += d;
            diff[(size_t)L] = run > 0.f ? d * (float)L / run : 1.f;   // cumulative-mean normalised
        }
        for (int L = minL; L <= maxL; ++L) {
            if (diff[(size_t)L] < 0.15f) {                            // first dip under threshold
                while (L + 1 <= maxL && diff[(size_t)(L + 1)] < diff[(size_t)L]) ++L;
                bestL = L; best = diff[(size_t)L]; break;
            }
            if (diff[(size_t)L] < best) { best = diff[(size_t)L]; bestL = L; }
        }
        if (bestL < 0 || best > 0.35f) { curT = 0.0055f * (float)sr; return; }   // unvoiced
        float l = (float)bestL;
        if (bestL > 1 && bestL < maxL) {                               // parabolic refinement
            float y0 = diff[(size_t)(bestL - 1)], y1 = diff[(size_t)bestL], y2 = diff[(size_t)(bestL + 1)];
            float den = y0 - 2.f * y1 + y2;
            if (std::fabs(den) > 1e-9f) l += 0.5f * (y0 - y2) / den;
        }
        float T = std::min(std::max(l * 4.f, (float)minP), (float)maxP);
        curT = (std::fabs(T - curT) < 0.2f * curT) ? 0.5f * (curT + T) : T;
    }

    inline void push(float x, bool analyse) {
        in[(size_t)(t & mask)] = x;
        decAcc += x;
        if (++decPhase == 4) { dec[(size_t)(dcount & dmask)] = decAcc * 0.25f; ++dcount; decAcc = 0.f; decPhase = 0; }
        if (analyse && (t & 255) == 255) detect();
        ++t;
    }

    // Keep the buffers current while the effect is off (no pitch tracking, so it costs ~nothing),
    // so switching on is seamless.
    inline void feed(float x) { push(x, false); active = false; }

    inline float process(float x, float pitchRatio, float formantRatio) {
        push(x, true);
        const int64_t u = t - D;                        // output time
        if (!active) {                                  // (re)start the grain schedule
            active = true;
            ts = u + maxP + 1;
            am = (double)ts;
            for (int k = 0; k <= mask; ++k) { acc[(size_t)k] = 0.f; wsum[(size_t)k] = 0.f; }
        }
        const float f = std::min(std::max(formantRatio, 0.5f), 2.f);
        for (;;) {
            const int W = (int)curT;
            const int reach = (int)std::ceil((float)W * std::max(f, 1.f)) + 3;
            if (ts + reach > t) break;                  // grain's input isn't in yet
            if (ts - W <= u) ts = u + W + 1;            // fell behind (period jumped): skip ahead
            if (ts + reach > t) break;
            while (am + curT <= (double)ts) am += curT; // latest analysis mark at or before ts
            if ((double)ts - am > 4.0 * curT) am = (double)ts;
            const float invW = 1.f / (float)W;
            for (int k = -W; k <= W; ++k) {
                float w = 0.5f + 0.5f * std::cos(kPi * (float)k * invW);
                size_t o = (size_t)((ts + k) & mask);
                acc[o] += read(am + (double)k * f) * w;
                wsum[o] += w;
            }
            ts += (int64_t)std::max(curT / std::max(pitchRatio, 0.25f), 16.f);
        }
        if (u < 0) return 0.f;
        size_t o = (size_t)(u & mask);
        float y = acc[o] / std::max(wsum[o], 0.6f);
        acc[o] = 0.f; wsum[o] = 0.f;
        return y;
    }
};

struct Echo {
    std::vector<float> buf;
    int n = 0, w = 0;
    void init(int sr) { n = sr; buf.assign((size_t)n, 0.f); w = 0; }
    inline void feed(float x) { buf[(size_t)w] = x; if (++w >= n) w = 0; }
    inline float process(float x, int delay, float fb, float mix) {
        if (delay < 1) delay = 1;
        if (delay >= n) delay = n - 1;
        int r = w - delay; if (r < 0) r += n;
        float d = buf[(size_t)r];
        buf[(size_t)w] = Flush(x + fb * d);
        if (++w >= n) w = 0;
        return x + mix * d;
    }
};

struct Channel {
    Psola ps; Echo ec;
    Biquad hp1, hp2, lp1, lp2, tone;
};

// Soft limiter: untouched below 0.8, then bends smoothly towards 1.0 instead of clipping hard.
static inline float SoftLimit(float y) {
    float a = std::fabs(y);
    if (a <= 0.8f) return y;
    float l = 0.8f + 0.2f * std::tanh((a - 0.8f) * 5.f);
    return y < 0.f ? -l : l;
}

class Engine {
public:
    void init(int sampleRate, int channels) {
        sr_ = (float)sampleRate;
        chans_.assign((size_t)channels, Channel());
        for (auto& c : chans_) { c.ps.init(sampleRate); c.ec.init(sampleRate); }
        ringPhase_ = 0.f;
        lastHp_ = lastLp_ = lastDrive_ = -1.f;
    }

    // data: interleaved float32, processed in place. sound: mono, `frames` long (or null).
    void process(float* data, uint32_t frames, const Params& p, const float* sound) {
        const int ch = (int)chans_.size();
        if (ch == 0) return;
        const bool  shiftOn = p.voiceOn && (std::fabs(p.pitch) > 0.01f || std::fabs(p.formant) > 0.01f);
        const float ratio   = std::pow(2.f, p.pitch / 12.f);
        const float fratio  = std::pow(2.f, p.formant / 12.f);
        const bool  ringOn  = p.voiceOn && p.ringMix > 0.001f;
        const bool  hpOn    = p.voiceOn && p.hpHz > 20.f;
        const bool  lpOn    = p.voiceOn && p.lpHz > 20.f && p.lpHz < 0.45f * sr_;
        const bool  driveOn = p.voiceOn && p.drive > 0.01f;
        const float pre     = 1.f + p.drive * p.drive * 40.f;          // up to ~41x into the clipper
        const float bias    = 0.15f * p.drive;                         // a little asymmetry = warmer
        const float post    = 0.5f / std::tanh(0.5f * pre);            // keep the level roughly even
        const float tb      = std::tanh(bias);
        const bool  echoOn  = p.voiceOn && p.echoMix > 0.01f;
        const int   echoDel = (int)(p.echoMs * 0.001f * sr_);
        const float fb      = std::min(std::max(p.echoFb, 0.f), 0.9f);
        const float ringStep = kTwoPi * p.ringHz / sr_;

        if (hpOn && p.hpHz != lastHp_) {
            for (auto& c : chans_) { c.hp1.setHighPass(p.hpHz, sr_); c.hp2.setHighPass(p.hpHz, sr_); }
            lastHp_ = p.hpHz;
        }
        if (lpOn && p.lpHz != lastLp_) {
            for (auto& c : chans_) { c.lp1.setLowPass(p.lpHz, sr_); c.lp2.setLowPass(p.lpHz, sr_); }
            lastLp_ = p.lpHz;
        }
        if (driveOn && p.drive != lastDrive_) {     // tame the fizz distortion adds up top
            for (auto& c : chans_) c.tone.setLowPass(9000.f - 4000.f * p.drive, sr_);
            lastDrive_ = p.drive;
        }

        for (uint32_t f = 0; f < frames; ++f) {
            float rm = 0.f;
            if (ringOn) {
                rm = std::sin(ringPhase_);
                ringPhase_ += ringStep;
                if (ringPhase_ > kTwoPi) ringPhase_ -= kTwoPi;
            }
            const float s = (sound && p.soundOn) ? sound[f] * p.soundGain : 0.f;
            for (int c = 0; c < ch; ++c) {
                Channel& C = chans_[(size_t)c];
                float x = data[(size_t)f * ch + c] * p.micGain;
                if (p.micMute) x = 0.f;
                if (p.voiceOn) {
                    if (shiftOn) x = C.ps.process(x, ratio, fratio); else C.ps.feed(x);
                    if (ringOn)  x = x * (1.f - p.ringMix) + x * rm * p.ringMix;
                    if (hpOn)  { x = C.hp1.process(x); x = C.hp2.process(x); }
                    if (lpOn)  { x = C.lp1.process(x); x = C.lp2.process(x); }
                    if (driveOn) x = C.tone.process((std::tanh(pre * x + bias) - tb) * post);
                    if (echoOn) x = C.ec.process(x, echoDel, fb, p.echoMix); else C.ec.feed(x);
                } else {
                    C.ps.feed(x);
                }
                data[(size_t)f * ch + c] = SoftLimit(x + s);
            }
        }
    }
private:
    float sr_ = 48000.f, ringPhase_ = 0.f;
    float lastHp_ = -1.f, lastLp_ = -1.f, lastDrive_ = -1.f;
    std::vector<Channel> chans_;
};

} // namespace vf
