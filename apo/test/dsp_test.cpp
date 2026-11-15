// Offline checks for dsp.h, run by CI before a release is built. Exit code != 0 fails the build.
#include "../src/dsp.h"
#include <cstdio>
#include <cmath>
#include <vector>

static const int kSr = 48000;

// A buzzy "voice": 150 Hz with falling harmonics, plus a little vibrato.
static std::vector<float> Voice(float hz, int n) {
    std::vector<float> v((size_t)n);
    double ph = 0;
    for (int i = 0; i < n; ++i) {
        double f = hz * (1.0 + 0.01 * std::sin(2 * 3.14159265 * 5.0 * i / kSr));
        ph += 2 * 3.14159265 * f / kSr;
        float s = 0;
        for (int h = 1; h <= 20; ++h) s += (float)(std::sin(h * ph) / h);
        v[(size_t)i] = 0.15f * s;
    }
    return v;
}

static std::vector<float> Run(const std::vector<float>& x, const vf::Params& p) {
    vf::Engine e; e.init(kSr, 1);
    std::vector<float> y = x;
    for (size_t i = 0; i < y.size(); i += 480) e.process(&y[i], (uint32_t)std::min<size_t>(480, y.size() - i), p, nullptr);
    return y;
}

// Fundamental by autocorrelation over the last half second.
static float Pitch(const std::vector<float>& y) {
    size_t s = y.size() - kSr / 2, n = kSr / 4;
    int best = 0; double bestV = -1;
    for (int lag = kSr / 600; lag <= kSr / 60; ++lag) {
        double a = 0, e1 = 0, e2 = 0;
        for (size_t i = 0; i < n; ++i) { a += y[s + i] * y[s + i + lag]; e1 += y[s + i] * y[s + i]; e2 += y[s + i + lag] * y[s + i + lag]; }
        double v = a / std::sqrt(e1 * e2 + 1e-12);
        if (v > bestV) { bestV = v; best = lag; }
    }
    // prefer the shortest lag that is nearly as good (avoid octave-down picks)
    for (int lag = kSr / 600; lag < best; ++lag) {
        double a = 0, e1 = 0, e2 = 0;
        for (size_t i = 0; i < n; ++i) { a += y[s + i] * y[s + i + lag]; e1 += y[s + i] * y[s + i]; e2 += y[s + i + lag] * y[s + i + lag]; }
        if (a / std::sqrt(e1 * e2 + 1e-12) > 0.9 * bestV) { best = lag; break; }
    }
    return (float)kSr / (float)best;
}

static float Rms(const std::vector<float>& y) {
    double a = 0; size_t s = y.size() / 2;
    for (size_t i = s; i < y.size(); ++i) a += (double)y[i] * y[i];
    return (float)std::sqrt(a / (double)(y.size() - s));
}

static bool Sane(const std::vector<float>& y) {
    for (float v : y) if (!std::isfinite(v) || std::fabs(v) > 1.0001f) return false;
    return true;
}

static int g_fail = 0;
static void Check(bool ok, const char* what, double got) {
    std::printf("%-44s %s  (%.2f)\n", what, ok ? "ok  " : "FAIL", got);
    if (!ok) ++g_fail;
}

int main() {
    auto x = Voice(150.f, kSr * 2);
    float in = Rms(x);
    vf::Params p; p.voiceOn = true;

    { auto y = Run(x, p); Check(Sane(y) && std::fabs(Rms(y) - in) < 0.01f, "no effect passes through", Rms(y)); }

    for (float st : { -12.f, -5.f, 5.f, 12.f }) {
        vf::Params q = p; q.pitch = st;
        auto y = Run(x, q);
        float want = 150.f * std::pow(2.f, st / 12.f), got = Pitch(y);
        char label[64]; std::snprintf(label, 64, "pitch %+.0f st -> %.0f Hz", st, want);
        Check(Sane(y) && std::fabs(got / want - 1.f) < 0.04f && Rms(y) > 0.3f * in, label, got);
    }
    for (float st : { -6.f, 6.f }) {
        vf::Params q = p; q.formant = st;
        auto y = Run(x, q);
        char label[64]; std::snprintf(label, 64, "formant %+.0f st keeps pitch 150 Hz", st);
        Check(Sane(y) && std::fabs(Pitch(y) / 150.f - 1.f) < 0.04f && Rms(y) > 0.3f * in, label, Pitch(y));
    }
    { vf::Params q = p; q.hpHz = 1000; auto y = Run(x, q);
      Check(Sane(y) && Rms(y) < 0.5f * in, "low cut 1 kHz removes most of a 150 Hz voice", Rms(y) / in); }
    { vf::Params q = p; q.lpHz = 250; auto y = Run(x, q);
      Check(Sane(y) && Rms(y) < 0.85f * in && Rms(y) > 0.3f * in, "high cut 250 Hz keeps fundamental only", Rms(y) / in); }
    { vf::Params q = p; q.drive = 0.8f; auto y = Run(x, q);
      double d = 0; for (size_t i = y.size() / 2; i < y.size(); ++i) d += std::fabs(y[i] - x[i]);
      Check(Sane(y) && d / (double)(y.size() / 2) > 0.02, "distortion changes the waveform", d / (double)(y.size() / 2)); }
    { vf::Params q = p; q.micGain = 20.f; auto y = Run(x, q);
      Check(Sane(y), "limiter keeps a 20x-gain signal under full scale", Rms(y)); }

    std::printf(g_fail ? "%d check(s) failed\n" : "all checks passed\n", g_fail);
    return g_fail ? 1 : 0;
}
