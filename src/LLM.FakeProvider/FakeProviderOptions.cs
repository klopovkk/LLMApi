namespace LLM.FakeProvider;

/// <summary>How the fake provider behaves. Bound from the <c>FakeProvider</c> configuration section.</summary>
public sealed class FakeProviderOptions
{
    public const string SectionName = "FakeProvider";

    public FakeProviderMode Mode { get; set; } = FakeProviderMode.Respond;

    /// <summary>Simulated answering latency before the single callback attempt.</summary>
    public TimeSpan Delay { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Base address the completion is posted to. In the composed environment each instance is
    /// given its peer's address, which guarantees the cross-instance callback path on every
    /// message rather than leaving it to a load balancer's chance distribution.
    /// </summary>
    public string CallbackBaseUri { get; set; } = "http://localhost";
}

public enum FakeProviderMode
{
    /// <summary>Answer after the delay.</summary>
    Respond,

    /// <summary>Report a provider-side failure after the delay.</summary>
    Fail,

    /// <summary>
    /// Accept the submission and never post. How the expiry and held-message scenarios are set up
    /// without touching the code under test.
    /// </summary>
    NeverRespond,
}
