CREATE OR REPLACE PACKAGE pkg_payment_match AS
  /* ------------------------------------------------------------------
     FinPro - nightly payment matching batch (Oracle 9i)

     FICTIONAL LEGACY CODE written for the payment-matching-ai reference
     project. Source only - it is not executed anywhere. The Delphi
     module PAYMATCH (legacy/delphi) mirrors this logic and is the
     reference behaviour.
     ------------------------------------------------------------------
     2004-11  MV  initial version
     2005-04  PK  logic copied to Delphi client (PAYMATCH)
     ------------------------------------------------------------------ */

  c_tol_czk          CONSTANT NUMBER := 1;      -- rounding tolerance
  c_adv_overpay_max  CONSTANT NUMBER := 5000;   -- advance invoice overpayment limit
  c_home_currency    CONSTANT VARCHAR2(3) := 'CZK';

  -- keeps digits only and strips leading zeros
  FUNCTION normalize_vs (p_vs IN VARCHAR2) RETURN VARCHAR2;

  -- "fa 2026-1234" -> 2026001234, NULL when nothing found
  FUNCTION parse_vs_from_message (p_msg IN VARCHAR2) RETURN VARCHAR2;

  -- matches one row of BANK_STATEMENT, writes result to PAYMENT_MATCH_RESULT
  PROCEDURE match_payment (p_payment_id IN VARCHAR2);

  -- nightly job entry point
  PROCEDURE run_batch;
END pkg_payment_match;
/
