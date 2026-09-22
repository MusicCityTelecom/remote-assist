using System.Diagnostics;
using System.Runtime.InteropServices;
using SharpGen.Runtime;
using Vortice.MediaFoundation;

namespace RemoteAssist.Agent;

internal sealed record H264EncodedFrame(
    byte[] Data,
    bool KeyFrame,
    long TimestampMicroseconds);

internal sealed class H264MediaFoundationEncoder : IDisposable
{
    private const int NeedMoreInputHResult = unchecked((int)0xC00D6D72);
    private const int ProgressiveInterlaceMode = 2;
    private const int BaselineProfile = 66;

    private readonly IMFActivate _activation;
    private readonly IMFTransform _transform;
    private readonly bool _providesSamples;
    private readonly bool _async;
    private readonly IMFMediaEventGenerator? _events;
    private readonly int _outputBufferSize;
    private readonly int _width;
    private readonly int _height;
    private readonly int _fps;
    private readonly Queue<(byte[] Data, bool KeyFrame, long TimestampMicroseconds)> _asyncReady = new();
    private byte[]? _sequenceHeader;
    private int _inputCredit;
    private bool _disposed;

    public int Width => _width;
    public int Height => _height;
    public int Fps => _fps;
    public int Bitrate { get; }
    public string Name { get; }
    public bool Hardware { get; }

    private H264MediaFoundationEncoder(
        IMFActivate activation,
        IMFTransform transform,
        string name,
        bool hardware,
        bool asyncTransform,
        int width,
        int height,
        int fps,
        int bitrate)
    {
        _activation = activation;
        _transform = transform;
        Name = name;
        Hardware = hardware;
        _async = asyncTransform;
        _width = width;
        _height = height;
        _fps = fps;
        Bitrate = bitrate;

        if (_async)
        {
            var attributes = transform.Attributes;
            attributes.Set(
                TransformAttributeKeys.TransformAsyncUnlock,
                1u).CheckError();
            try
            {
                attributes.Set(
                    SinkWriterAttributeKeys.LowLatency,
                    1u).CheckError();
            }
            catch { }

            _events = transform.QueryInterface<IMFMediaEventGenerator>();
        }

        ConfigureTransform(transform, width, height, fps, bitrate);

        var streamInfo = transform.GetOutputStreamInfo(0);
        _providesSamples =
            (streamInfo.Flags & (int)OutputStreamInfoFlags.OutputStreamProvidesSamples) != 0;
        _outputBufferSize = Math.Max(streamInfo.Size, Math.Max(1 << 20, width * height));

        transform.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, UIntPtr.Zero);
        transform.ProcessMessage(TMessageType.MessageNotifyStartOfStream, UIntPtr.Zero);
    }

    public static H264MediaFoundationEncoder? TryCreate(
        int width,
        int height,
        int fps,
        int bitrate,
        out string? error)
    {
        error = null;
        if (width < 2 || height < 2 || (width & 1) != 0 || (height & 1) != 0)
        {
            error = $"H.264 requires even frame dimensions; received {width}x{height}.";
            return null;
        }

        MediaFoundationRuntime.EnsureStarted();

        var encoder = TryCreateFromCandidates(
            width,
            height,
            fps,
            bitrate,
            (uint)(EnumFlag.EnumFlagHardware |
                   EnumFlag.EnumFlagSyncmft |
                   EnumFlag.EnumFlagAsyncmft |
                   EnumFlag.EnumFlagSortandfilter),
            hardware: true,
            out var hardwareError);

        if (encoder is not null)
            return encoder;

        encoder = TryCreateFromCandidates(
            width,
            height,
            fps,
            bitrate,
            (uint)(EnumFlag.EnumFlagSyncmft |
                   EnumFlag.EnumFlagAsyncmft |
                   EnumFlag.EnumFlagLocalmft |
                   EnumFlag.EnumFlagSortandfilter),
            hardware: false,
            out var softwareError);

        if (encoder is null)
            error = softwareError ?? hardwareError ??
                $"No Media Foundation H.264 encoder accepted {width}x{height} at {fps} FPS.";

        return encoder;
    }

    private static H264MediaFoundationEncoder? TryCreateFromCandidates(
        int width,
        int height,
        int fps,
        int bitrate,
        uint flags,
        bool hardware,
        out string? error)
    {
        error = null;
        var sawCandidate = false;
        var output = new RegisterTypeInfo
        {
            GuidMajorType = MediaTypeGuids.Video,
            GuidSubtype = VideoFormatGuids.H264
        };

        using var candidates = MediaFactory.MFTEnumEx(
            TransformCategoryGuids.VideoEncoder,
            flags,
            null,
            output);

        foreach (var candidate in candidates)
        {
            sawCandidate = true;
            IMFActivate? retained = null;
            IMFTransform? transform = null;
            var candidateName = hardware ? "hardware H.264 encoder" : "H.264 encoder";
            try
            {
                retained = new IMFActivate(candidate.NativePointer);
                Marshal.AddRef(retained.NativePointer);

                transform = candidate.ActivateObject<IMFTransform>();

                var attributes = transform.Attributes;
                var asyncTransform =
                    attributes.GetUInt32(
                        TransformAttributeKeys.TransformAsync,
                        out var isAsync).Success &&
                    isAsync != 0;

                var name = ReadFriendlyName(candidate);
                candidateName = string.IsNullOrWhiteSpace(name) ? candidateName : name;
                var built = new H264MediaFoundationEncoder(
                    retained,
                    transform,
                    name,
                    hardware,
                    asyncTransform,
                    width,
                    height,
                    Math.Clamp(fps, 2, 30),
                    Math.Clamp(bitrate, 250_000, 20_000_000));

                retained = null;
                transform = null;
                return built;
            }
            catch (Exception ex)
            {
                error = $"{candidateName}: {ex.GetType().Name}: {ex.Message}";
                transform?.Dispose();
                if (retained is not null)
                {
                    try { retained.ShutdownObject(); } catch { }
                    retained.Dispose();
                }
            }
        }

        if (!sawCandidate)
            error = hardware
                ? "No hardware Media Foundation H.264 encoder candidates were found."
                : "No Media Foundation H.264 encoder candidates were found.";

        return null;
    }

    public H264EncodedFrame? Encode(
        CapturedBgraFrame frame,
        long timestampMicroseconds)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (frame.Width != _width || frame.Height != _height)
            throw new ArgumentException(
                "Captured frame dimensions do not match the H.264 encoder.");

        if (_async)
            return EncodeAsync(frame, timestampMicroseconds);

        using var input = CreateInputSample(
            frame,
            timestampMicroseconds);

        try
        {
            _transform.ProcessInput(0, input, 0);
        }
        catch (SharpGenException)
        {
            return null;
        }

        var encoded = TryReadOutput();
        if (encoded is null)
            return null;

        return FinalizeEncoded(
            encoded.Value.Data,
            encoded.Value.KeyFrame,
            encoded.Value.TimestampMicroseconds);
    }

    private H264EncodedFrame EncodeAsync(
        CapturedBgraFrame frame,
        long timestampMicroseconds)
    {
        var budgetMs = Math.Clamp(
            (1000 / Math.Max(1, _fps)) * 2,
            40,
            160);

        if (!WaitForInputCredit(budgetMs))
            throw new TimeoutException(
                "Asynchronous H.264 encoder did not request input.");

        using var input = CreateInputSample(
            frame,
            timestampMicroseconds);

        _inputCredit--;
        _transform.ProcessInput(0, input, 0);

        var started = Stopwatch.GetTimestamp();
        while (Stopwatch.GetElapsedTime(started).TotalMilliseconds < budgetMs)
        {
            PumpAsyncEvents();

            if (_asyncReady.Count > 0)
            {
                var encoded = _asyncReady.Dequeue();
                return FinalizeEncoded(
                    encoded.Data,
                    encoded.KeyFrame,
                    encoded.TimestampMicroseconds);
            }

            Thread.Sleep(1);
        }

        throw new TimeoutException(
            "Asynchronous H.264 encoder did not produce output in time.");
    }

    private bool WaitForInputCredit(int timeoutMs)
    {
        if (_inputCredit > 0)
            return true;

        var started = Stopwatch.GetTimestamp();
        while (Stopwatch.GetElapsedTime(started).TotalMilliseconds < timeoutMs)
        {
            PumpAsyncEvents();
            if (_inputCredit > 0)
                return true;

            Thread.Sleep(1);
        }

        return false;
    }

    private void PumpAsyncEvents()
    {
        if (_events is null)
            return;

        for (var i = 0; i < 16; i++)
        {
            IMFMediaEvent mediaEvent;
            try
            {
                mediaEvent = _events.GetEvent(1);
            }
            catch (SharpGenException ex)
                when (ex.ResultCode ==
                      Vortice.MediaFoundation.ResultCode.NoEventsAvailable)
            {
                return;
            }

            using (mediaEvent)
            {
                mediaEvent.Status.CheckError();

                switch (mediaEvent.EventType)
                {
                    case MediaEventTypes.TransformNeedInput:
                        _inputCredit = Math.Min(8, _inputCredit + 1);
                        break;

                    case MediaEventTypes.TransformHaveOutput:
                    {
                        var encoded = TryReadOutput();
                        if (encoded is not null)
                        {
                            _asyncReady.Enqueue((
                                encoded.Value.Data,
                                encoded.Value.KeyFrame,
                                encoded.Value.TimestampMicroseconds));
                        }
                        break;
                    }
                }
            }
        }
    }

    private H264EncodedFrame FinalizeEncoded(
        byte[] data,
        bool reportedKeyFrame,
        long timestampMicroseconds)
    {
        if (_sequenceHeader is null)
            _sequenceHeader = ReadSequenceHeader();

        var annexB = H264AnnexB.Normalize(data);
        var keyFrame =
            reportedKeyFrame ||
            H264AnnexB.ContainsNalType(annexB, 5);

        if (keyFrame &&
            _sequenceHeader is { Length: > 0 } header &&
            (!H264AnnexB.ContainsNalType(annexB, 7) ||
             !H264AnnexB.ContainsNalType(annexB, 8)))
        {
            var normalizedHeader = H264AnnexB.Normalize(header);
            var combined = new byte[
                normalizedHeader.Length + annexB.Length];

            Buffer.BlockCopy(
                normalizedHeader,
                0,
                combined,
                0,
                normalizedHeader.Length);
            Buffer.BlockCopy(
                annexB,
                0,
                combined,
                normalizedHeader.Length,
                annexB.Length);

            annexB = combined;
        }

        return new H264EncodedFrame(
            annexB,
            keyFrame,
            timestampMicroseconds);
    }

    private (byte[] Data, bool KeyFrame, long TimestampMicroseconds)? TryReadOutput()
    {
        IMFSample? clientSample = null;
        var output = new OutputDataBuffer
        {
            StreamID = 0,
            Status = 0,
            Sample = null!,
            Events = null!
        };

        try
        {
            if (!_providesSamples)
            {
                clientSample = MediaFactory.MFCreateSample();
                using var buffer = MediaFactory.MFCreateMemoryBuffer(_outputBufferSize);
                clientSample.AddBuffer(buffer);
                output.Sample = clientSample;
            }

            var result = _transform.ProcessOutput(
                ProcessOutputFlags.None,
                1,
                ref output,
                out _);

            if (result.Failure)
            {
                if (result.Code == NeedMoreInputHResult)
                    return null;

                result.CheckError();
            }

            var sample = output.Sample ?? clientSample;
            if (sample is null || sample.TotalLength <= 0)
                return null;

            using var contiguous = sample.ConvertToContiguousBuffer();
            var length = contiguous.CurrentLength;
            if (length <= 0)
                return null;

            var bytes = new byte[length];
            contiguous.Lock(out var source, out _, out _);
            try
            {
                Marshal.Copy(source, bytes, 0, length);
            }
            finally
            {
                contiguous.Unlock();
            }

            var cleanPoint =
                sample.GetUInt32(
                    SampleAttributeKeys.CleanPoint,
                    out var clean).Success &&
                clean != 0;

            var timestampMicroseconds =
                sample.SampleTime / 10;

            return (
                bytes,
                cleanPoint,
                timestampMicroseconds);
        }
        finally
        {
            output.Events?.Dispose();

            if (output.Sample is not null &&
                !ReferenceEquals(output.Sample, clientSample))
                output.Sample.Dispose();

            clientSample?.Dispose();
        }
    }

    private IMFSample CreateInputSample(
        CapturedBgraFrame frame,
        long timestampMicroseconds)
    {
        var nv12 = ConvertToNv12(frame);

        var sample = MediaFactory.MFCreateSample();
        IMFMediaBuffer? buffer = null;
        try
        {
            buffer = MediaFactory.MFCreateMemoryBuffer(nv12.Length);
            buffer.Lock(out var destination, out _, out _);
            try
            {
                Marshal.Copy(nv12, 0, destination, nv12.Length);
            }
            finally
            {
                buffer.Unlock();
            }

            buffer.CurrentLength = nv12.Length;
            sample.AddBuffer(buffer);
            sample.SampleTime = timestampMicroseconds * 10;
            sample.SampleDuration = 10_000_000L / _fps;
            return sample;
        }
        catch
        {
            sample.Dispose();
            throw;
        }
        finally
        {
            buffer?.Dispose();
        }
    }

    private static void ConfigureTransform(
        IMFTransform transform,
        int width,
        int height,
        int fps,
        int bitrate)
    {
        using var output = MediaFactory.MFCreateMediaType();
        SetVideoType(output, VideoFormatGuids.H264, width, height, fps);
        output.Set(
            MediaTypeAttributeKeys.AvgBitrate,
            checked((uint)bitrate)).CheckError();

        try
        {
            output.Set(
                MediaTypeAttributeKeys.Mpeg2Profile,
                checked((uint)BaselineProfile)).CheckError();
        }
        catch { }

        transform.SetOutputType(0, output, 0);

        using var input = MediaFactory.MFCreateMediaType();
        SetVideoType(input, VideoFormatGuids.NV12, width, height, fps);
        transform.SetInputType(0, input, 0);
    }

    private static void SetVideoType(
        IMFMediaType mediaType,
        Guid subtype,
        int width,
        int height,
        int fps)
    {
        mediaType.Set(
            MediaTypeAttributeKeys.MajorType,
            MediaTypeGuids.Video).CheckError();
        mediaType.Set(
            MediaTypeAttributeKeys.Subtype,
            subtype).CheckError();

        MediaFactory.MFSetAttributeSize(
            mediaType,
            MediaTypeAttributeKeys.FrameSize,
            checked((uint)width),
            checked((uint)height)).CheckError();

        MediaFactory.MFSetAttributeRatio(
            mediaType,
            MediaTypeAttributeKeys.FrameRate,
            checked((uint)fps),
            1).CheckError();

        MediaFactory.MFSetAttributeRatio(
            mediaType,
            MediaTypeAttributeKeys.PixelAspectRatio,
            1,
            1).CheckError();

        mediaType.Set(
            MediaTypeAttributeKeys.InterlaceMode,
            checked((uint)ProgressiveInterlaceMode)).CheckError();
    }

    private byte[]? ReadSequenceHeader()
    {
        try
        {
            using var output = _transform.GetOutputCurrentType(0);
            return output.GetBlob(MediaTypeAttributeKeys.MpegSequenceHeader);
        }
        catch
        {
            return null;
        }
    }

    private static string ReadFriendlyName(IMFActivate activation)
    {
        try
        {
            return activation.GetString(
                TransformAttributeKeys.MftFriendlyNameAttribute);
        }
        catch
        {
            return "Media Foundation H.264 encoder";
        }
    }

    private static byte[] ConvertToNv12(CapturedBgraFrame frame)
    {
        var width = frame.Width;
        var height = frame.Height;
        var src = frame.Data;
        var ySize = checked(width * height);
        var output = new byte[checked(ySize + ySize / 2)];

        Parallel.For(0, height, y =>
        {
            var row = y * frame.Stride;
            var yRow = y * width;

            for (var x = 0; x < width; x++)
            {
                var p = row + x * 4;
                var b = src[p];
                var g = src[p + 1];
                var r = src[p + 2];

                var value =
                    ((66 * r + 129 * g + 25 * b + 128) >> 8) + 16;

                output[yRow + x] =
                    (byte)Math.Clamp(value, 0, 255);
            }
        });

        Parallel.For(0, height / 2, yHalf =>
        {
            var y = yHalf * 2;
            var row0 = y * frame.Stride;
            var row1 = Math.Min(y + 1, height - 1) * frame.Stride;
            var uvRow = ySize + yHalf * width;

            for (var x = 0; x < width; x += 2)
            {
                var p00 = row0 + x * 4;
                var p01 = row0 + Math.Min(x + 1, width - 1) * 4;
                var p10 = row1 + x * 4;
                var p11 = row1 + Math.Min(x + 1, width - 1) * 4;

                var b =
                    (src[p00] + src[p01] + src[p10] + src[p11]) >> 2;
                var g =
                    (src[p00 + 1] + src[p01 + 1] +
                     src[p10 + 1] + src[p11 + 1]) >> 2;
                var r =
                    (src[p00 + 2] + src[p01 + 2] +
                     src[p10 + 2] + src[p11 + 2]) >> 2;

                var u =
                    ((-38 * r - 74 * g + 112 * b + 128) >> 8) + 128;
                var v =
                    ((112 * r - 94 * g - 18 * b + 128) >> 8) + 128;

                output[uvRow + x] =
                    (byte)Math.Clamp(u, 0, 255);
                output[uvRow + x + 1] =
                    (byte)Math.Clamp(v, 0, 255);
            }
        });

        return output;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            _transform.ProcessMessage(
                TMessageType.MessageNotifyEndOfStream,
                UIntPtr.Zero);
            _transform.ProcessMessage(
                TMessageType.MessageNotifyEndStreaming,
                UIntPtr.Zero);
        }
        catch { }

        try { _events?.Dispose(); } catch { }
        _asyncReady.Clear();
        _transform.Dispose();

        try { _activation.ShutdownObject(); } catch { }
        _activation.Dispose();
    }
}
