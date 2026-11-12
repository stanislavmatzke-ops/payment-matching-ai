unit uMatchEngine;

{ The matching itself. Mirrors PKG_PAYMENT_MATCH.MATCH_PAYMENT from the
  old Oracle batch (see legacy/plsql). Keep both in sync!! }

interface

procedure ProcessPayments;

implementation

uses
  SysUtils, uGlobals, uVsParser;

procedure ProcessPayments;
var
  p, k, Found: Integer;
  VS, How: string;
  FromField: Boolean;
begin
  for p := 0 to g_PayCount - 1 do
  begin
    with g_Payments[p] do
    begin
      Status := ST_NONE;
      InvNo := '';
      Rule := '';
      ResDiff := 0;
      HasDiff := False;

      { --- 1. bank's own movements (interest, fee refunds) -----------
        booked by the bank interface, not by us }
      if Pos(c_BankName, UpperCase(Name)) > 0 then
      begin
        Status := ST_SKIP;
        Rule := '-/BANK_OWN';
        Continue;
      end;

      { --- 2. foreign currency ---------------------------------------
        2005-09 PK: accountant books these by hand with the daily rate }
      if UpperCase(Trim(Currency)) <> c_HomeCurrency then
      begin
        Rule := '-/CURRENCY';
        Continue;
      end;

      { --- 3. which VS -------------------------------------------------- }
      VS := NormalizeVS(PayVS);
      FromField := VS <> '';
      How := 'FIELD';
      if VS = '' then
      begin
        VS := ExtractVSFromMessage(Msg);
        How := 'MSG';
      end;
      if VS = '' then
      begin
        Rule := '-/NO_VS';
        Continue;
      end;

      { --- 4. find the invoice ------------------------------------------ }
      Found := -1;
      for k := 0 to g_InvCount - 1 do
        if g_Invoices[k].VS = VS then
        begin
          Found := k;
          Break;
        end;

      // 2006-02 PK: some banks cut the VS, so try to find it inside
      if (Found < 0) and (Length(VS) >= c_FuzzyMinLen) then
        for k := 0 to g_InvCount - 1 do
          if Pos(VS, g_Invoices[k].VS) > 0 then
          begin
            Found := k;
            How := 'FUZZY';
            Break;
          end;

      if Found < 0 then
      begin
        Rule := How + '/NOT_FOUND';
        Continue;
      end;

      if g_Invoices[Found].Paid then
      begin
        Rule := How + '/DUPLICATE';
        Continue;
      end;

      { --- 5. amount ---------------------------------------------------- }
      ResDiff := Amount - g_Invoices[Found].Amount;
      HasDiff := True;

      if Abs(ResDiff) < c_TolCZK then
      begin
        Status := ST_PAIRED;
        Rule := How + '/EXACT';
      end
      else if ResDiff < 0 then
      begin
        // 2007-03 JN: partial payment is OK when the customer typed the VS
        if FromField then
        begin
          Status := ST_PARTIAL;
          Rule := How + '/PARTIAL';
        end
        else
          Rule := How + '/AMOUNT';
      end
      else
      begin
        // 2007-03 JN: advance invoices - small overpayment stays as credit
        if (g_Invoices[Found].Kind = c_KindAdvance) and (ResDiff <= c_AdvOverpayMax) then
        begin
          Status := ST_PAIRED;
          Rule := How + '/ADV_OVERPAY';
        end
        else
          Rule := How + '/OVERPAID';
      end;

      if (Status = ST_PAIRED) or (Status = ST_PARTIAL) then
      begin
        g_Invoices[Found].Paid := True;
        InvNo := g_Invoices[Found].Number;
      end;
    end;
  end;
end;

end.
