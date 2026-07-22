-- A reporting function that returns the set of customers who qualify for a rewards program in
-- the current month: those with at least a minimum number of monthly purchases and a minimum
-- dollar amount spent. It RETURNS SETOF customer — i.e. rows shaped like the customer table —
-- and builds its result into a TEMPORARY table before selecting it back out, illustrating
-- dynamic set-returning plpgsql. Marked SECURITY DEFINER in the canonical schema so it can be
-- granted to reporting roles.
CREATE FUNCTION rewards_report(
    min_monthly_purchases integer,
    min_dollar_amount_purchased numeric
)
RETURNS SETOF customer
LANGUAGE plpgsql
SECURITY DEFINER
AS $$
DECLARE
    last_month_start date;
    last_month_end   date;
    rr               RECORD;
    tmpSQL           text;
BEGIN
    -- Validate the arguments.
    IF min_monthly_purchases = 0 THEN
        RAISE EXCEPTION 'Minimum monthly purchases parameter must be > 0';
    END IF;
    IF min_dollar_amount_purchased = 0.00 THEN
        RAISE EXCEPTION 'Minimum monthly dollar amount purchased parameter must be > $0.00';
    END IF;

    -- Determine the bounds of the previous calendar month.
    last_month_start := CURRENT_DATE - '3 month'::interval;
    last_month_start := to_date(
        (extract(YEAR FROM last_month_start) || '-' || extract(MONTH FROM last_month_start) || '-01'),
        'YYYY-MM-DD');
    last_month_end := last_day(last_month_start);

    -- Collect the qualifying customer ids into a temporary table.
    CREATE TEMPORARY TABLE tmpCustomer (customer_id integer NOT NULL PRIMARY KEY);

    INSERT INTO tmpCustomer (customer_id)
    SELECT p.customer_id
    FROM payment AS p
    WHERE date(p.payment_date) BETWEEN last_month_start AND last_month_end
    GROUP BY customer_id
    HAVING SUM(p.amount) > min_dollar_amount_purchased
       AND COUNT(customer_id) > min_monthly_purchases;

    -- Return the full customer rows for the qualifying ids.
    tmpSQL := 'SELECT * FROM customer WHERE customer_id IN (SELECT customer_id FROM tmpCustomer)';
    FOR rr IN EXECUTE tmpSQL LOOP
        RETURN NEXT rr;
    END LOOP;

    DROP TABLE tmpCustomer;

    RETURN;
END
$$;
