namespace PaymentMatching.DataGen.Model;

/// <summary>One incoming payment line of a bank statement. Every text field is untrusted input.</summary>
public sealed record BankTransaction(
    string PaymentId,
    DateOnly BookingDate,
    decimal Amount,
    string Currency,
    string CounterpartyIban,
    string CounterpartyName,
    string VariableSymbol,
    string ConstantSymbol,
    string SpecificSymbol,
    string Message);
