-- Returns the last day of the month containing the given timestamp. A small, IMMUTABLE SQL
-- function used by the rentals reporting helpers. It works by truncating to the first of the
-- month, advancing one month, and subtracting a day.
CREATE FUNCTION last_day(timestamp)
RETURNS date
LANGUAGE sql
IMMUTABLE
STRICT
AS $$
SELECT (date_trunc('month', $1) + INTERVAL '1 month' - INTERVAL '1 day')::date
$$;
