// C interface between the SwiftUI app and the C++ audio core (CashVoiceCore.cpp).
#pragma once
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

typedef struct {
    int   voiceOn, micMute, soundOn;
    float pitch, formant, ringHz, ringMix, echoMs, echoFb, echoMix, hpHz, lpHz, drive;
    float micGain, soundGain;
} CVParams;

typedef struct {
    int   driverInstalled;    // the CashVoice Mic/Feed devices exist
    int   micInUse;           // some app is recording from CashVoice Mic
    int   running;            // we're capturing the real mic and feeding CashVoice Mic
    int   permissionDenied;   // microphone access was refused
    float inputRate;          // sample rate of the real mic
    float inputLevel;         // recent peak of the real mic, 0..1
    char  inputName[128];
    char  error[256];
} CVStatus;

void cv_init(void);
void cv_set_params(const CVParams* p);
/// The real microphone to use (its CoreAudio UID), or "" for the system default
/// (skipping CashVoice Mic itself).
void cv_set_input(const char* uid);
/// Call a few times a second: starts/stops capture as apps start/stop using CashVoice Mic.
void cv_tick(void);
void cv_get_status(CVStatus* s);

/// Play a clip (mono, 48 kHz). micGain/localGain <= 0 leaves that destination out.
void cv_play(const float* mono48k, int frames, float micGain, float localGain);
void cv_stop_sounds(void);
void cv_set_local_volume(float v);
/// Hear yourself: on/off, with the voice effect or not, volume.
void cv_set_monitor(int on, int wet, float volume);

/// Real input devices as "uid\tname\n" lines (CashVoice's own devices left out). Returns bytes written.
int cv_list_inputs(char* buf, int len);

void cv_shutdown(void);

#ifdef __cplusplus
}
#endif
