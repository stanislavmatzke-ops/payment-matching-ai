unit uCsv;

{ Hand written CSV helpers. Separator ';', fields may be quoted.
  NOTE: quoted fields spanning more lines are NOT supported. }

interface

uses
  Classes, SysUtils;

var
  g_FS: TFormatSettings;

procedure SplitLine(const S: string; Fields: TStrings);
function StrToAmt(const S: string): Double;
function AmtToStr(D: Double): string;
function CsvQuote(const S: string): string;

implementation

procedure SplitLine(const S: string; Fields: TStrings);
var
  i: Integer;
  Cur: string;
  InQ: Boolean;
begin
  Fields.Clear;
  Cur := '';
  InQ := False;
  i := 1;
  while i <= Length(S) do
  begin
    if InQ then
    begin
      if S[i] = '"' then
      begin
        if (i < Length(S)) and (S[i + 1] = '"') then
        begin
          Cur := Cur + '"';
          Inc(i);
        end
        else
          InQ := False;
      end
      else
        Cur := Cur + S[i];
    end
    else if S[i] = '"' then
      InQ := True
    else if S[i] = ';' then
    begin
      Fields.Add(Cur);
      Cur := '';
    end
    else
      Cur := Cur + S[i];
    Inc(i);
  end;
  Fields.Add(Cur);
end;

function StrToAmt(const S: string): Double;
begin
  // TODO: report bad amounts instead of zero
  Result := StrToFloatDef(Trim(S), 0, g_FS);
end;

function AmtToStr(D: Double): string;
begin
  Result := FormatFloat('0.00', D, g_FS);
end;

function CsvQuote(const S: string): string;
begin
  if (Pos(';', S) > 0) or (Pos('"', S) > 0) then
    Result := '"' + StringReplace(S, '"', '""', [rfReplaceAll]) + '"'
  else
    Result := S;
end;

initialization
{$IFDEF FPC}
  g_FS := DefaultFormatSettings;
{$ELSE}
  GetLocaleFormatSettings(0, g_FS);
{$ENDIF}
  g_FS.DecimalSeparator := '.';

end.
