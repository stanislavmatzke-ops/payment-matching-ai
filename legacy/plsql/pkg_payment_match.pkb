CREATE OR REPLACE PACKAGE BODY pkg_payment_match AS

  FUNCTION normalize_vs (p_vs IN VARCHAR2) RETURN VARCHAR2 IS
    v_vs VARCHAR2(30);
  BEGIN
    v_vs := REGEXP_REPLACE(p_vs, '[^0-9]', '');
    v_vs := LTRIM(v_vs, '0');
    IF v_vs IS NULL AND p_vs IS NOT NULL AND INSTR(p_vs, '0') > 0 THEN
      v_vs := '0';
    END IF;
    RETURN v_vs;
  END normalize_vs;

  FUNCTION parse_vs_from_message (p_msg IN VARCHAR2) RETURN VARCHAR2 IS
    v_msg  VARCHAR2(4000) := UPPER(p_msg);
    v_pos  PLS_INTEGER;
    v_num  VARCHAR2(20);
  BEGIN
    v_pos := INSTR(v_msg, 'FA');
    IF v_pos = 0 THEN
      v_pos := INSTR(v_msg, 'FV');
    END IF;
    IF v_pos = 0 THEN
      RETURN NULL;
    END IF;

    -- number YYYY-NNNN or YYYY/NNNN at most 15 chars after the keyword
    v_num := REGEXP_SUBSTR(SUBSTR(v_msg, v_pos + 2, 15 + 11), '[0-9]{4}[-/][0-9]{1,6}');
    IF v_num IS NULL OR TO_NUMBER(SUBSTR(v_num, 1, 4)) < 1990 THEN
      RETURN NULL;
    END IF;

    RETURN SUBSTR(v_num, 1, 4) || LPAD(SUBSTR(v_num, 6), 6, '0');
  END parse_vs_from_message;

  PROCEDURE save_result (
    p_payment_id IN VARCHAR2,
    p_status     IN VARCHAR2,
    p_invoice_no IN VARCHAR2,
    p_rule       IN VARCHAR2,
    p_diff       IN NUMBER
  ) IS
  BEGIN
    INSERT INTO payment_match_result (payment_id, status, invoice_no, rule_code, amount_diff, created_at)
    VALUES (p_payment_id, p_status, p_invoice_no, p_rule, p_diff, SYSDATE);
  END save_result;

  PROCEDURE match_payment (p_payment_id IN VARCHAR2) IS
    r_pay       bank_statement%ROWTYPE;
    r_inv       invoice%ROWTYPE;
    v_vs        VARCHAR2(30);
    v_how       VARCHAR2(10) := 'FIELD';
    v_from_fld  BOOLEAN;
    v_diff      NUMBER;
  BEGIN
    SELECT * INTO r_pay FROM bank_statement WHERE payment_id = p_payment_id;

    -- bank's own movements are booked by the bank interface
    IF INSTR(UPPER(r_pay.counterparty_name), 'DEMO BANK') > 0 THEN
      save_result(p_payment_id, 'X', NULL, '-/BANK_OWN', NULL);
      RETURN;
    END IF;

    IF UPPER(TRIM(r_pay.currency)) <> c_home_currency THEN
      save_result(p_payment_id, 'N', NULL, '-/CURRENCY', NULL);
      RETURN;
    END IF;

    v_vs := normalize_vs(r_pay.variable_symbol);
    v_from_fld := v_vs IS NOT NULL;
    IF v_vs IS NULL THEN
      v_vs := parse_vs_from_message(r_pay.message);
      v_how := 'MSG';
    END IF;
    IF v_vs IS NULL THEN
      save_result(p_payment_id, 'N', NULL, '-/NO_VS', NULL);
      RETURN;
    END IF;

    BEGIN
      SELECT * INTO r_inv
        FROM invoice
       WHERE normalize_vs(variable_symbol) = v_vs
         AND ROWNUM = 1;
    EXCEPTION
      WHEN NO_DATA_FOUND THEN
        IF LENGTH(v_vs) >= 8 THEN
          BEGIN
            -- bank sometimes cuts the VS
            SELECT * INTO r_inv
              FROM (SELECT * FROM invoice
                     WHERE INSTR(normalize_vs(variable_symbol), v_vs) > 0
                     ORDER BY invoice_number)
             WHERE ROWNUM = 1;
            v_how := 'FUZZY';
          EXCEPTION
            WHEN NO_DATA_FOUND THEN
              save_result(p_payment_id, 'N', NULL, v_how || '/NOT_FOUND', NULL);
              RETURN;
          END;
        ELSE
          save_result(p_payment_id, 'N', NULL, v_how || '/NOT_FOUND', NULL);
          RETURN;
        END IF;
    END;

    IF r_inv.paid_flag = 'Y' THEN
      save_result(p_payment_id, 'N', NULL, v_how || '/DUPLICATE', NULL);
      RETURN;
    END IF;

    v_diff := r_pay.amount - r_inv.amount;

    IF ABS(v_diff) <= c_tol_czk THEN
      save_result(p_payment_id, 'P', r_inv.invoice_number, v_how || '/EXACT', v_diff);
    ELSIF v_diff < 0 THEN
      IF v_from_fld THEN
        save_result(p_payment_id, 'C', r_inv.invoice_number, v_how || '/PARTIAL', v_diff);
      ELSE
        save_result(p_payment_id, 'N', NULL, v_how || '/AMOUNT', v_diff);
        RETURN;
      END IF;
    ELSIF r_inv.kind = 'Advance' AND v_diff <= c_adv_overpay_max THEN
      save_result(p_payment_id, 'P', r_inv.invoice_number, v_how || '/ADV_OVERPAY', v_diff);
    ELSE
      save_result(p_payment_id, 'N', NULL, v_how || '/OVERPAID', v_diff);
      RETURN;
    END IF;

    UPDATE invoice SET paid_flag = 'Y' WHERE invoice_number = r_inv.invoice_number;
  END match_payment;

  PROCEDURE run_batch IS
  BEGIN
    FOR r IN (SELECT payment_id FROM bank_statement
               WHERE processed_flag = 'N'
               ORDER BY payment_id) LOOP
      match_payment(r.payment_id);
      UPDATE bank_statement SET processed_flag = 'Y' WHERE payment_id = r.payment_id;
    END LOOP;
    COMMIT;
  END run_batch;

END pkg_payment_match;
/
