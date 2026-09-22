using System;
using System.Collections.Generic;
using System.Linq;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace VUMixer.Audio;

/// <summary>
/// Endpoint discovery + change notifications. Callbacks arrive on the COM
/// thread: consumers should dispatch (UI) / use thread-safe reopen guards.
/// </summary>
public sealed class DeviceWatcher : IDisposable, IMMNotificationClient
{
    private readonly MMDeviceEnumerator _enumerator = new();

    public event Action? DevicesChanged;            // added / removed / state change
    public event Action<string?>? DefaultOutputChanged; // new default render id

    public void Start() => _enumerator.RegisterEndpointNotificationCallback(this);

    public List<MMDevice> CaptureDevices() => Active(_enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active));

    public List<MMDevice> RenderDevices() => Active(_enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active));

    public MMDevice? DefaultRender()
    {
        try { return _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia); }
        catch { return null; }
    }

    public bool Exists(string? deviceId)
        => deviceId != null &&
           (CaptureDevices().Any(d => d.ID == deviceId) || RenderDevices().Any(d => d.ID == deviceId));

    private static List<MMDevice> Active(MMDeviceCollection collection)
        => collection.Where(d => d.State == DeviceState.Active).ToList();

    // -- IMMNotificationClient (COM thread) ---------------------------------
    void IMMNotificationClient.OnDeviceStateChanged(string deviceId, DeviceState newState) => DevicesChanged?.Invoke();
    void IMMNotificationClient.OnDeviceAdded(string pwstrDeviceId) => DevicesChanged?.Invoke();
    void IMMNotificationClient.OnDeviceRemoved(string pwstrDeviceId) => DevicesChanged?.Invoke();
    void IMMNotificationClient.OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
    {
        if (flow == DataFlow.Render) DefaultOutputChanged?.Invoke(defaultDeviceId);
    }
    void IMMNotificationClient.OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }

    public void Dispose()
    {
        try { _enumerator.UnregisterEndpointNotificationCallback(this); } catch { /* already unregistered */ }
        _enumerator.Dispose();
    }
}