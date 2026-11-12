namespace PaymentMatching.Data;

/// <summary>A row that belongs to one tenant. The data layer filters and checks every such row.</summary>
public interface ITenantOwned
{
    Guid TenantId { get; set; }
}

/// <summary>One sandbox. Deleting it deletes all its rows (cascade).</summary>
public sealed class TenantRow
{
    public Guid Id { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset LastActiveAt { get; set; }

    /// <summary>Seed of the generated starter data (reproducible sandbox).</summary>
    public int DataSeed { get; set; }
}

/// <summary>
/// The person behind a tenant: the minimum from the sign-in (SPEC 9b). (Provider, Subject) is the
/// stable identity; name and e-mail are only shown back to the user.
/// </summary>
public sealed class UserRow : ITenantOwned
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public required string Provider { get; set; }

    public required string Subject { get; set; }

    public required string Name { get; set; }

    public required string Email { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class CustomerRow : ITenantOwned
{
    public Guid TenantId { get; set; }

    public required string Id { get; set; }

    public required string Name { get; set; }

    public required string Iban { get; set; }
}

public sealed class InvoiceRow : ITenantOwned
{
    public Guid TenantId { get; set; }

    public required string Number { get; set; }

    public required string VariableSymbol { get; set; }

    public required string CustomerId { get; set; }

    public required string Kind { get; set; }

    public DateOnly DueDate { get; set; }

    public decimal Amount { get; set; }

    public required string Currency { get; set; }
}

/// <summary>One bank statement line. Every text field is untrusted input.</summary>
public sealed class PaymentRow : ITenantOwned
{
    public Guid TenantId { get; set; }

    public required string Id { get; set; }

    /// <summary>Statement order (earlier payments claim invoices first).</summary>
    public int Seq { get; set; }

    public DateOnly BookingDate { get; set; }

    public decimal Amount { get; set; }

    public required string Currency { get; set; }

    public required string CounterpartyIban { get; set; }

    public required string CounterpartyName { get; set; }

    public required string VariableSymbol { get; set; }

    public required string Message { get; set; }

    /// <summary>The line as received; its hash is the idempotency key.</summary>
    public required string RawLine { get; set; }
}

/// <summary>
/// The audit record of one decision. The primary key (tenant, idempotency key) means a payment line
/// is decided once per tenant; the full record is kept as JSON.
/// </summary>
public sealed class DecisionRow : ITenantOwned
{
    public Guid TenantId { get; set; }

    public required string IdempotencyKey { get; set; }

    public required string PaymentId { get; set; }

    public required string Outcome { get; set; }

    public DateTimeOffset DecidedAt { get; set; }

    public required string Record { get; set; }
}

/// <summary>Primary key (tenant, invoice): an invoice is matched automatically at most once.</summary>
public sealed class InvoiceMatchRow : ITenantOwned
{
    public Guid TenantId { get; set; }

    public required string InvoiceNumber { get; set; }

    public required string PaymentId { get; set; }

    public required string DecisionKey { get; set; }
}

/// <summary>A proposal for a person (never a match). Primary key (tenant, id) makes it idempotent.</summary>
public sealed class ProposalRow : ITenantOwned
{
    public Guid TenantId { get; set; }

    public required string Id { get; set; }

    public required string PaymentId { get; set; }

    public DateTimeOffset At { get; set; }

    public required string Record { get; set; }
}
