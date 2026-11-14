#include "../src/dsp.h"
#include <cstdio>
#include <cstdlib>
int main(int argc, char** argv) {
    float pitch = argc > 1 ? (float)atof(argv[1]) : 12.f;
    int sr = 48000, N = sr * 2;
    vf::Engine e; e.init(sr, 1);
    vf::Params p; p.voiceOn = true; p.pitch = pitch;
    std::vector<float> d(N);
    for (int i = 0; i < N; ++i) d[i] = 0.5f * std::sin(6.2831853f * 440.f * i / sr);
    for (int off = 0; off < N; off += 480) e.process(d.data() + off, 480, p, nullptr);
    FILE* f = fopen("out.raw", "wb"); fwrite(d.data(), 4, N, f); fclose(f);
    return 0;
}
