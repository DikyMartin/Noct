using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Noct.Core.Audio;

/// <summary>
/// Enumerates WASAPI render endpoints and installed ASIO drivers, and probes which formats a
/// device will accept. Nothing here reports a capability the device did not confirm.
/// </summary>
public sealed class OutputDeviceCatalog : IDisposable
{
    private readonly object _gate = new();
    private MMDeviceEnumerator? _enumerator;
    private MMDeviceNotificationClient? _notifications;
    private bool _disposed;

    /// <summary>Raised when endpoints are added, removed or change state. Fired on a system thread.</summary>
    public event EventHandler? DevicesChanged;

    public IReadOnlyList<OutputDeviceRef> Enumerate()
    {
        var devices = new List<OutputDeviceRef>();
        devices.AddRange(EnumerateWasapi());
        devices.AddRange(EnumerateAsio());
        return devices;
    }

    public IReadOnlyList<OutputDeviceRef> EnumerateWasapi()
    {
        var result = new List<OutputDeviceRef>();
        try
        {
            var enumerator = GetEnumerator();
            string? defaultId = null;
            if (enumerator.TryGetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia, out var defaultDevice))
            {
                using (defaultDevice)
                {
                    defaultId = defaultDevice.ID;
                }
            }

            using var collection = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
            foreach (var device in collection)
            {
                using (device)
                {
                    var name = SafeName(device);
                    var id = device.ID;
                    var isDefault = id == defaultId;

                    // The same endpoint is offered twice: once shared, once exclusive. Whether
                    // exclusive actually works is only known once a format is negotiated.
                    result.Add(new OutputDeviceRef(id, name, OutputBackend.WasapiShared, isDefault));
                    result.Add(new OutputDeviceRef(id, name, OutputBackend.WasapiExclusive, isDefault));
                }
            }
        }
        catch (Exception ex) when (ex is COMException or CoreAudioException)
        {
            // An unusable audio stack must not take the app down; the caller shows an empty list.
        }

        return result;
    }

    public IReadOnlyList<OutputDeviceRef> EnumerateAsio()
    {
        var result = new List<OutputDeviceRef>();
        try
        {
            if (!AsioOut.IsSupported())
            {
                return result;
            }

            foreach (var name in AsioOut.GetDriverNames())
            {
                if (!string.IsNullOrWhiteSpace(name))
                {
                    result.Add(new OutputDeviceRef(name, name, OutputBackend.Asio, IsDefault: false));
                }
            }
        }
        catch (Exception)
        {
            // A broken ASIO registry entry leaves WASAPI untouched.
        }

        return result;
    }

    /// <summary>Resolves a stored device key, falling back to the system default render endpoint.</summary>
    public OutputDeviceRef? Resolve(string? deviceKey, OutputBackend backend)
    {
        var devices = Enumerate();

        if (!string.IsNullOrEmpty(deviceKey))
        {
            var exact = devices.FirstOrDefault(d => d.Key == deviceKey);
            if (exact is not null)
            {
                return exact;
            }
        }

        return devices.FirstOrDefault(d => d.Backend == backend && d.IsDefault)
               ?? devices.FirstOrDefault(d => d.Backend == backend)
               ?? devices.FirstOrDefault(d => d.Backend == OutputBackend.WasapiShared);
    }

    /// <summary>The device's current shared-mode mix format, or null when it cannot be read.</summary>
    public AudioFormat? GetMixFormat(string deviceId)
    {
        try
        {
            using var device = GetEnumerator().GetDevice(deviceId);
            using var client = device.CreateAudioClient();
            return AudioFormat.FromWaveFormat(client.MixFormat);
        }
        catch (Exception ex) when (ex is COMException or CoreAudioException)
        {
            return null;
        }
    }

    /// <summary>
    /// Asks the endpoint which of a set of candidate formats it accepts in the given share mode.
    /// Used for exclusive-mode negotiation and for the settings diagnostics view.
    /// </summary>
    public IReadOnlyList<FormatProbe> ProbeFormats(string deviceId, AudioClientShareMode shareMode, IEnumerable<WaveFormat> candidates)
    {
        var results = new List<FormatProbe>();
        MMDevice? device = null;
        AudioClient? client = null;

        try
        {
            device = GetEnumerator().GetDevice(deviceId);
            client = device.CreateAudioClient();
        }
        catch (Exception ex)
        {
            foreach (var candidate in candidates)
            {
                results.Add(new FormatProbe(AudioFormat.FromWaveFormat(candidate), false, null, Classify(ex).Message));
            }

            client?.Dispose();
            device?.Dispose();
            return results;
        }

        try
        {
            foreach (var candidate in candidates)
            {
                try
                {
                    var supported = client.IsFormatSupported(shareMode, candidate, out var closest);
                    results.Add(new FormatProbe(
                        AudioFormat.FromWaveFormat(candidate),
                        supported,
                        closest is null ? null : AudioFormat.FromWaveFormat(closest),
                        null));
                }
                catch (Exception ex)
                {
                    results.Add(new FormatProbe(AudioFormat.FromWaveFormat(candidate), false, null, Classify(ex).Message));
                }
            }
        }
        finally
        {
            client.Dispose();
            device.Dispose();
        }

        return results;
    }

    public void StartWatching()
    {
        lock (_gate)
        {
            if (_disposed || _notifications is not null)
            {
                return;
            }

            try
            {
                _notifications = GetEnumerator().CreateNotificationClient(useSynchronizationContext: false);
                _notifications.DeviceAdded += OnDeviceEvent;
                _notifications.DeviceRemoved += OnDeviceEvent;
                _notifications.DeviceStateChanged += OnDeviceEvent;
                _notifications.DefaultDeviceChanged += OnDeviceEvent;
            }
            catch (Exception)
            {
                _notifications = null;
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            if (_notifications is not null)
            {
                _notifications.DeviceAdded -= OnDeviceEvent;
                _notifications.DeviceRemoved -= OnDeviceEvent;
                _notifications.DeviceStateChanged -= OnDeviceEvent;
                _notifications.DefaultDeviceChanged -= OnDeviceEvent;
                _notifications.Dispose();
                _notifications = null;
            }

            _enumerator?.Dispose();
            _enumerator = null;
        }
    }

    public MMDeviceEnumerator GetEnumerator()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _enumerator ??= new MMDeviceEnumerator();
        }
    }

    public static (OutputFailureKind Kind, string Message) Classify(Exception ex) => ex switch
    {
        AudioDeviceInUseException =>
            (OutputFailureKind.DeviceInUse, "The device is already in use by another application in exclusive mode."),
        AudioExclusiveModeNotAllowedException =>
            (OutputFailureKind.ExclusiveModeNotAllowed, "Windows does not allow exclusive access to this device. Enable \"Allow applications to take exclusive control\" in its sound properties."),
        AudioFormatNotSupportedException formatEx =>
            (OutputFailureKind.FormatNotSupported, $"The device rejected the requested format{(formatEx.SuggestedFormat is null ? "" : $" and suggested {AudioFormat.FromWaveFormat(formatEx.SuggestedFormat).Describe()}")}."),
        AudioDeviceDisconnectedException =>
            (OutputFailureKind.DeviceUnavailable, "The device was disconnected."),
        CoreAudioException core =>
            (OutputFailureKind.DriverError, $"Windows audio reported an error (0x{core.HResult:X8}): {core.Message}"),
        COMException com =>
            (OutputFailureKind.DriverError, $"Audio driver error (0x{com.HResult:X8}): {com.Message}"),
        _ => (OutputFailureKind.DriverError, ex.Message),
    };

    private static string SafeName(MMDevice device)
    {
        try
        {
            var friendly = device.FriendlyName;
            return string.IsNullOrWhiteSpace(friendly) ? device.ID : friendly;
        }
        catch (Exception)
        {
            return device.ID;
        }
    }

    private void OnDeviceEvent(object? sender, EventArgs e) => DevicesChanged?.Invoke(this, EventArgs.Empty);
}

/// <summary>One answer from <see cref="OutputDeviceCatalog.ProbeFormats"/>.</summary>
public sealed record FormatProbe(AudioFormat Requested, bool Supported, AudioFormat? ClosestMatch, string? Error);
