// sound on|off: mutes or unmutes the game in Windows' mixer (its default audio session on the default output device), the
// way the volume mixer would. The game itself is left alone: its sound engine keeps running, nothing reaches the speakers.
// Windows keeps an app's mute after it exits (per executable and device, under HKCU\...\Audio\PolicyConfig\PropertyStore),
// so the plugin unmutes when it loads and `kh stop` before it ends the game: a game started by hand is never left silent.
#define COBJMACROS
#include "harness.h"
#include <initguid.h>
#include <mmdeviceapi.h>
#include <audiopolicy.h>
#include <string.h>

// The process's default audio session on the default output device, or null with the failing call's name in `step`.
static ISimpleAudioVolume* session_volume(const char** step)
{
    IMMDeviceEnumerator* devices = NULL;
    IMMDevice* device = NULL;
    IAudioSessionManager* sessions = NULL;
    ISimpleAudioVolume* volume = NULL;

    *step = "CoCreateInstance(MMDeviceEnumerator)";
    if (FAILED(CoCreateInstance(&CLSID_MMDeviceEnumerator, NULL, CLSCTX_ALL, &IID_IMMDeviceEnumerator, (void**)&devices))) goto done;

    *step = "GetDefaultAudioEndpoint";
    if (FAILED(IMMDeviceEnumerator_GetDefaultAudioEndpoint(devices, eRender, eConsole, &device))) goto done;

    *step = "Activate(IAudioSessionManager)";
    if (FAILED(IMMDevice_Activate(device, &IID_IAudioSessionManager, CLSCTX_ALL, NULL, (void**)&sessions))) goto done;

    *step = "GetSimpleAudioVolume";
    if (FAILED(IAudioSessionManager_GetSimpleAudioVolume(sessions, NULL, FALSE, &volume))) volume = NULL;

done:
    if (sessions) IAudioSessionManager_Release(sessions);
    if (device) IMMDevice_Release(device);
    if (devices) IMMDeviceEnumerator_Release(devices);
    return volume;
}

int sound_set(int on, int* muted, const char** why)
{
    // The game thread has COM already (the sound engine uses it); this balances only what it adds.
    HRESULT com = CoInitializeEx(NULL, COINIT_MULTITHREADED);
    ISimpleAudioVolume* volume = session_volume(why);
    BOOL state = FALSE;

    if (volume)
    {
        ISimpleAudioVolume_SetMute(volume, !on, NULL);
        ISimpleAudioVolume_GetMute(volume, &state);
        ISimpleAudioVolume_Release(volume);
    }

    if (SUCCEEDED(com)) CoUninitialize();

    *muted = state;
    return volume != NULL;
}

int cmd_sound(Command* c)
{
    const char* what = arg(c, 0, "");
    const char* why;
    int muted;
    if (_stricmp(what, "on") != 0 && _stricmp(what, "off") != 0) return fail(c, "usage: sound on | off");

    if (!sound_set(_stricmp(what, "on") == 0, &muted, &why)) return fail(c, "no audio session: %s failed", why);

    text_add(&c->reply, "{\"muted\":%s}", muted ? "true" : "false");
    return 1;
}
