namespace PaymentMatching.Data;

/// <summary>
/// The tenant (sandbox) whose data the current unit of work may see. In the web app it comes only
/// from the signed-in user's cookie; in the MCP server from the operator who starts the process.
/// Never from a request parameter or a tool argument.
/// </summary>
public interface ITenantContext
{
    /// <summary>The current tenant. Throws when none is set: no tenant means no data (fail closed).</summary>
    Guid TenantId { get; }

    /// <summary>True for the public demo: nothing may be saved through this context.</summary>
    bool IsReadOnly => false;
}

/// <summary>Scoped holder: set once per request or process, never switched to another tenant.</summary>
public sealed class TenantContext : ITenantContext
{
    private Guid? _tenantId;

    public TenantContext()
    {
    }

    public TenantContext(Guid tenantId) => Set(tenantId);

    public Guid TenantId => _tenantId ?? throw new TenantIsolationException("No tenant is set for this operation.");

    public bool IsSet => _tenantId is not null;

    public bool IsReadOnly { get; private init; }

    /// <summary>The demo sandbox for visitors without sign-in: reading only, saving throws.</summary>
    public static TenantContext ReadOnlyFor(Guid tenantId) => new(tenantId) { IsReadOnly = true };

    public void Set(Guid tenantId)
    {
        if (tenantId == Guid.Empty)
        {
            throw new ArgumentException("Empty tenant id.", nameof(tenantId));
        }

        if (_tenantId is { } current && current != tenantId)
        {
            throw new TenantIsolationException("The tenant of this operation is already set and cannot change.");
        }

        _tenantId = tenantId;
    }
}

/// <summary>An attempt to read or write data outside the current tenant, or without a tenant.</summary>
public sealed class TenantIsolationException(string message) : InvalidOperationException(message);
