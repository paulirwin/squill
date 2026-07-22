-- The trigger function behind every "last_update" column. It runs BEFORE UPDATE (see the
-- last-updated triggers under Triggers/) and stamps NEW.last_update with the current time, so
-- application code never has to set it. As a trigger function it takes no arguments and
-- returns the special "trigger" pseudo-type.
CREATE FUNCTION last_updated()
RETURNS trigger
LANGUAGE plpgsql
AS $$
BEGIN
    NEW.last_update = now();
    RETURN NEW;
END
$$;
