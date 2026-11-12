program PayMatch;

{ FinPro PAYMATCH - command line batch.
  Usage: PayMatch <input-dir> <output-file>
  <input-dir> must contain invoices.csv and bank_statement.csv. }

{$APPTYPE CONSOLE}

uses
  Classes,
  SysUtils,
  uGlobals in 'uGlobals.pas',
  uCsv in 'uCsv.pas',
  uVsParser in 'uVsParser.pas',
  uMatchEngine in 'uMatchEngine.pas';

procedure LoadInvoices(const FileName: string);
var
  F: TextFile;
  Line: string;
  Flds: TStringList;
  First: Boolean;
begin
  Flds := TStringList.Create;
  try
    AssignFile(F, FileName);
    Reset(F);
    First := True;
    while not Eof(F) do
    begin
      ReadLn(F, Line);
      if First then
      begin
        First := False;   // header
        Continue;
      end;
      if Trim(Line) = '' then
        Continue;
      SplitLine(Line, Flds);
      if Flds.Count < 8 then
        Continue;         // broken line, skip silently
      if g_InvCount >= Length(g_Invoices) then
        SetLength(g_Invoices, g_InvCount + 256);
      with g_Invoices[g_InvCount] do
      begin
        Number := Flds[0];
        VS := NormalizeVS(Flds[1]);
        CustomerId := Flds[2];
        Kind := Flds[3];
        Amount := StrToAmt(Flds[6]);
        Currency := Flds[7];
        Paid := False;
      end;
      Inc(g_InvCount);
    end;
    CloseFile(F);
  finally
    Flds.Free;
  end;
end;

procedure LoadPayments(const FileName: string);
var
  F: TextFile;
  Line: string;
  Flds: TStringList;
  First: Boolean;
begin
  Flds := TStringList.Create;
  try
    AssignFile(F, FileName);
    Reset(F);
    First := True;
    while not Eof(F) do
    begin
      ReadLn(F, Line);
      if First then
      begin
        First := False;
        Continue;
      end;
      if Trim(Line) = '' then
        Continue;
      SplitLine(Line, Flds);
      if Flds.Count < 10 then
        Continue;
      if g_PayCount >= Length(g_Payments) then
        SetLength(g_Payments, g_PayCount + 256);
      with g_Payments[g_PayCount] do
      begin
        Id := Flds[0];
        Amount := StrToAmt(Flds[2]);
        Currency := Flds[3];
        Iban := Flds[4];
        Name := Flds[5];
        PayVS := Flds[6];
        KS := Flds[7];
        SS := Flds[8];
        Msg := Flds[9];
      end;
      Inc(g_PayCount);
    end;
    CloseFile(F);
  finally
    Flds.Free;
  end;
end;

procedure WriteResults(const FileName: string);
var
  F: TextFile;
  p: Integer;
  DiffStr: string;
begin
  AssignFile(F, FileName);
  Rewrite(F);
  WriteLn(F, 'payment_id;status;invoice_numbers;rule;amount_diff');
  for p := 0 to g_PayCount - 1 do
    with g_Payments[p] do
    begin
      if HasDiff then
        DiffStr := AmtToStr(ResDiff)
      else
        DiffStr := '';
      WriteLn(F, Id + ';' + Status + ';' + InvNo + ';' + CsvQuote(Rule) + ';' + DiffStr);
    end;
  CloseFile(F);
end;

procedure PrintSummary;
var
  p, nP, nC, nN, nX: Integer;
begin
  nP := 0; nC := 0; nN := 0; nX := 0;
  for p := 0 to g_PayCount - 1 do
    case g_Payments[p].Status of
      ST_PAIRED:  Inc(nP);
      ST_PARTIAL: Inc(nC);
      ST_SKIP:    Inc(nX);
    else
      Inc(nN);
    end;
  WriteLn('PAYMATCH: ', g_InvCount, ' invoices, ', g_PayCount, ' payments');
  WriteLn('  P paired   ', nP);
  WriteLn('  C partial  ', nC);
  WriteLn('  N none     ', nN);
  WriteLn('  X skipped  ', nX);
end;

var
  InDir: string;
begin
  if ParamCount < 2 then
  begin
    WriteLn('Usage: PayMatch <input-dir> <output-file>');
    Halt(2);
  end;
  InDir := IncludeTrailingPathDelimiter(ParamStr(1));

  LoadInvoices(InDir + 'invoices.csv');
  LoadPayments(InDir + 'bank_statement.csv');
  ProcessPayments;
  WriteResults(ParamStr(2));
  PrintSummary;
end.
