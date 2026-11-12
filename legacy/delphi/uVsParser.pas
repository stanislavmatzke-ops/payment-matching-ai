unit uVsParser;

{ Variable symbol (VS) helpers. }

interface

function NormalizeVS(const S: string): string;
function ExtractVSFromMessage(const Msg: string): string;

implementation

uses
  SysUtils, uGlobals;

function NormalizeVS(const S: string): string;
var
  i: Integer;
begin
  Result := '';
  for i := 1 to Length(S) do
    if S[i] in ['0'..'9'] then
      Result := Result + S[i];

  // bank pads VS with zeros to 10 digits (2006-11 PK)
  while (Length(Result) > 1) and (Result[1] = '0') do
    Delete(Result, 1, 1);
end;

{ Customers write the invoice number into the message, e.g.
  "fa 2026-1234", "Faktura c. 2026/1234". Invoice number YYYY-NNNN
  maps to VS YYYY00NNNN. Returns '' when nothing usable is found. }
function ExtractVSFromMessage(const Msg: string): string;
var
  U, Year, Seq: string;
  P, i, Limit: Integer;
begin
  Result := '';
  U := UpperCase(Msg);

  P := Pos('FA', U);              // FA, FAKTURA, FAKTURU ...
  if P = 0 then
    P := Pos('FV', U);
  if P = 0 then
    Exit;

  i := P + 2;
  Limit := P + 2 + c_MsgScanLen;
  while (i <= Length(U)) and (i < Limit) and not (U[i] in ['0'..'9']) do
    Inc(i);
  if (i > Length(U)) or not (U[i] in ['0'..'9']) then
    Exit;

  Year := Copy(U, i, 4);
  if (Length(Year) <> 4) or (StrToIntDef(Year, -1) < c_MinYear) then
    Exit;
  i := i + 4;

  if (i > Length(U)) or not (U[i] in ['-', '/']) then
    Exit;
  Inc(i);

  Seq := '';
  while (i <= Length(U)) and (U[i] in ['0'..'9']) and (Length(Seq) < 6) do
  begin
    Seq := Seq + U[i];
    Inc(i);
  end;
  if Seq = '' then
    Exit;

  while Length(Seq) < 6 do
    Seq := '0' + Seq;
  Result := Year + Seq;
end;

end.
