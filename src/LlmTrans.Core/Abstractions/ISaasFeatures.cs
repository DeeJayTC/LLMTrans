namespace LlmTrans.Core.Abstractions;

public enum Edition { SelfHosted, Saas }

/// Switches SaaS-only capabilities on/off. Self-host build uses a no-op implementation;
/// the SaaS package replaces it with a real one. `EditionGuard` middleware checks these
/// before dispatching SaaS-only endpoints.
public interface ISaasFeatures
{
    Edition Edition { get; }
    bool IsMultiOrgEnabled { get; }
    bool IsBillingEnabled { get; }
    bool IsScimEnabled { get; }
}

public sealed class SelfHostFeatures : ISaasFeatures
{
    public Edition Edition => Edition.SelfHosted;
    public bool IsMultiOrgEnabled => false;
    public bool IsBillingEnabled => false;
    public bool IsScimEnabled => false;
}
