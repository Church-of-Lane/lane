using Lane.Audio.Capture;
using Lane.Audio.Recognition;
using Lane.Audio.Synthesis;
using Lane.Audio.Voiceprints;
using Lane.Core.Agent;
using Lane.Core.Identity;
using Lane.Core.Kernel;
using Lane.Core.Sessions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Lane.Audio;

public sealed class AudioSetupOptions
{
    public bool Enabled { get; set; }

    /// <summary>
    /// Where Lane's voice comes from: <c>elevenlabs</c> or <c>flite</c>.
    ///
    /// A string rather than an enum bound straight from configuration, because the binder
    /// leaves an enum at its default on a typo — and silently falling back to the paid
    /// provider is the wrong direction to fail in.
    /// </summary>
    public string Provider { get; set; } = "elevenlabs";

    /// <summary>What turns speech into words: <c>azure</c> or <c>openrouter</c>.</summary>
    public string Recognizer { get; set; } = "azure";

    public AzureSpeechOptions Recognition { get; set; } = new();
    public OpenRouterSpeechOptions OpenRouter { get; set; } = new();
    public ElevenLabsOptions  Synthesis   { get; set; } = new();
    public FliteOptions       Flite       { get; set; } = new();
    public BargeInOptions     BargeIn     { get; set; } = new();
    public VoiceprintOptions  Voiceprints { get; set; } = new();
    public MicrophoneOptions  Microphone  { get; set; } = new();

    /// <summary>Voice, tempo and pitch. The same shape v2's speech settings had.</summary>
    public SpeechOptions Speech { get; set; } = new("MEJe6hPrI48Kt2lFuVe3");

    /// <summary>Names the bad value and the good ones — the message is the whole point.</summary>
    public SpeechProvider ResolveProvider() => Provider?.Trim().ToLowerInvariant() switch
    {
        null or "" or "elevenlabs" => SpeechProvider.ElevenLabs,
        "flite"                    => SpeechProvider.Flite,

        _ => throw new InvalidOperationException(
            $"Lane:Audio:Provider is '{Provider}'. It must be 'elevenlabs' or 'flite'."),
    };

    public RecognitionProvider ResolveRecognizer() => Recognizer?.Trim().ToLowerInvariant() switch
    {
        null or "" or "azure" => RecognitionProvider.Azure,
        "openrouter"          => RecognitionProvider.OpenRouter,

        _ => throw new InvalidOperationException(
            $"Lane:Audio:Recognizer is '{Recognizer}'. It must be 'azure' or 'openrouter'."),
    };
}

public enum SpeechProvider
{
    ElevenLabs,
    Flite,
}

public enum RecognitionProvider
{
    Azure,
    OpenRouter,
}

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Gives Lane ears and a voice.
    ///
    /// Everything registered here is optional: without it, sessions have no voice output,
    /// the observer factory offers nothing, and every surface works exactly as it did
    /// before audio existed.
    /// </summary>
    public static IServiceCollection AddLaneAudio(this IServiceCollection services, AudioSetupOptions options)
    {
        if (!options.Enabled) return services;

        // Read now rather than inside the factory: an unknown provider is a configuration
        // mistake, and finding out at the first clause means finding out mid-conversation.
        SpeechProvider provider = options.ResolveProvider();
        RecognitionProvider recognition = options.ResolveRecognizer();

        services.AddSingleton(options);
        services.AddSingleton(options.Recognition);
        services.AddSingleton(options.Synthesis);
        services.AddSingleton(options.Flite);
        services.AddSingleton(options.BargeIn);

        services.TryAddSingleton<IVoiceFloor>(sp => new VoiceFloor(
            sp.GetRequiredService<IAgentKernel>,
            options.BargeIn,
            sp.GetRequiredService<ILogger<VoiceFloor>>()));

        services.TryAddSingleton<ISpeechSynthesizer>(sp => provider switch
        {
            SpeechProvider.Flite => new FliteSynthesizer(
                options.Flite, sp.GetRequiredService<ILogger<FliteSynthesizer>>()),

            _ => new ElevenLabsSynthesizer(
                options.Synthesis, sp.GetRequiredService<ILogger<ElevenLabsSynthesizer>>()),
        });

        // A recogniser per source, not one shared: each holds a connection and a session
        // with the recognition service for as long as its speaker is talking. Which kind
        // depends on the source — one person's headset and a microphone in a room are
        // different questions to ask the service, and asking the wrong one attributes a
        // whole room to whoever the source was registered as.
        if (recognition is RecognitionProvider.OpenRouter)
        {
            HttpClient http = new() { Timeout = TimeSpan.FromSeconds(60) };

            services.AddSingleton<Func<SpeakerAttribution, ISpeechRecognizer>>(sp => attribution =>
                new OpenRouterSpeechRecognizer(
                    http,
                    options.OpenRouter,
                    sp.GetRequiredService<ILogger<OpenRouterSpeechRecognizer>>(),
                    sampleVoices: attribution == SpeakerAttribution.Diarized && options.Voiceprints.Enabled));
        }
        else
        {
            services.AddSingleton<Func<SpeakerAttribution, ISpeechRecognizer>>(sp => attribution =>
                attribution == SpeakerAttribution.Diarized
                    ? new DiarizingSpeechRecognizer(
                        options.Recognition, sp.GetRequiredService<ILogger<DiarizingSpeechRecognizer>>())
                    : new AzureSpeechRecognizer(
                        options.Recognition, sp.GetRequiredService<ILogger<AzureSpeechRecognizer>>()));
        }

        services.AddSingleton(options.Voiceprints);

        // Without a voiceprint model, the people on one microphone are still told apart —
        // they just stop being anybody once the call ends. Loading the model is what turns
        // "two voices in this room" into "the voice that was here last week".
        if (options.Voiceprints.Enabled)
        {
            services.TryAddSingleton<IVoiceEncoder>(sp => new OnnxVoiceEncoder(
                options.Voiceprints, sp.GetRequiredService<ILogger<OnnxVoiceEncoder>>()));

            services.TryAddSingleton<ISpeakerAttributor>(sp => new VoiceprintAttributor(
                sp.GetRequiredService<IVoiceEncoder>(),
                sp.GetRequiredService<IVoiceprintDirectory>(),
                sp.GetRequiredService<IIdentityResolver>(),
                options.Voiceprints,
                sp.GetRequiredService<ILogger<VoiceprintAttributor>>()));
        }
        else
        {
            services.TryAddSingleton<ISpeakerAttributor>(sp => new LabelRosterAttributor(
                sp.GetRequiredService<IIdentityResolver>(),
                sp.GetRequiredService<ILogger<LabelRosterAttributor>>()));
        }

        services.AddSingleton(sp => new AudioRouter(
            sp.GetRequiredService<IAgentKernel>(),
            sp.GetRequiredService<Func<SpeakerAttribution, ISpeechRecognizer>>(),
            sp.GetRequiredService<IVoiceFloor>(),
            sp.GetRequiredService<ILogger<AudioRouter>>(),
            sp.GetRequiredService<ISpeakerAttributor>()));

        services.AddSingleton(options.Microphone);

        // The machine's own microphone, when it has been asked for. Registered shared: a
        // microphone in a room is the case worth having, and one person's headset already
        // has a socket.
        if (options.Microphone.Enabled)
            services.AddHostedService(sp => new MicrophoneService(
                options.Microphone,
                sp.GetRequiredService<AudioRouter>(),
                sp.GetRequiredService<ISessionRegistry>(),

                // The same floor the barge-in path uses, because it is the barge-in path
                // this is keeping her own voice out of.
                sp.GetRequiredService<IVoiceFloor>(),

                sp.GetRequiredService<ILoggerFactory>(),

                // Optional, and resolved here rather than required: whether anything is
                // showing the conversation is the host's business, and a room with nobody
                // watching it still answers out loud.
                sp.GetService<IRoomEcho>()));

        services.AddSingleton<IAgentObserverFactory>(sp => new VoiceObserverFactory(
            sp.GetRequiredService<ISpeechSynthesizer>(),
            options.Speech,
            sp.GetRequiredService<IVoiceFloor>(),
            sp.GetRequiredService<ILoggerFactory>()));

        return services;
    }
}
