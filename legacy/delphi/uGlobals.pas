unit uGlobals;

{ ------------------------------------------------------------------
  FinPro - payment matching module (PAYMATCH)
  Global constants, record types and shared state.

  FICTIONAL LEGACY CODE written for the payment-matching-ai reference
  project. Styled after a typical mid-2000s Delphi accounting module.
  ------------------------------------------------------------------
  2005-04  PK  initial version (ported from the PL/SQL batch)
  2005-09  PK  foreign currency payments excluded
  2006-02  PK  partial VS lookup (bank cuts leading digits)
  2007-03  JN  partial payments, advance invoice overpayment
  ------------------------------------------------------------------ }

interface

const
  c_TolCZK        = 1.00;     // rounding tolerance (halere)
  c_AdvOverpayMax = 5000.0;   // max overpayment accepted on advance invoice
  c_FuzzyMinLen   = 8;        // min VS length for partial lookup
  c_MinYear       = 1990;
  c_MsgScanLen    = 15;       // how far after "FA" we look for a number
  c_HomeCurrency  = 'CZK';
  c_BankName      = 'DEMO BANK';
  c_KindAdvance   = 'Advance';

  ST_PAIRED  = 'P';
  ST_PARTIAL = 'C';
  ST_NONE    = 'N';
  ST_SKIP    = 'X';

type
  TInvoiceRec = record
    Number:     string;
    VS:         string;
    CustomerId: string;
    Kind:       string;
    Amount:     Double;
    Currency:   string;
    Paid:       Boolean;
  end;

  TPaymentRec = record
    Id:       string;
    Amount:   Double;
    Currency: string;
    Iban:     string;
    Name:     string;
    PayVS:    string;
    KS:       string;
    SS:       string;
    Msg:      string;
    { results }
    Status:   Char;
    InvNo:    string;
    Rule:     string;
    ResDiff:  Double;
    HasDiff:  Boolean;
  end;

var
  g_Invoices: array of TInvoiceRec;
  g_InvCount: Integer = 0;
  g_Payments: array of TPaymentRec;
  g_PayCount: Integer = 0;

implementation

end.
