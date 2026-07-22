-- Round-trip fixture for CREATE TRIGGER (issue #83), modeled on Pagila's last_updated and
-- film_fulltext_trigger.
--
-- The row-change stamp is an integer "version" column rather than a timestamp: a bare
-- `timestamp` column does not yet round-trip through model extraction (the catalog reports
-- `timestamp without time zone`, which is an unrelated column-type gap), and this fixture is
-- about triggers, not column types.

CREATE TABLE film (
    film_id integer PRIMARY KEY,
    title text NOT NULL,
    description text,
    fulltext tsvector,
    version integer NOT NULL
);

-- The trigger function bumps the row's version on every change. Mirrors the shape of Pagila's
-- last_updated function (which stamps a timestamp instead).
CREATE FUNCTION bump_version() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
    BEGIN
        NEW.version = OLD.version + 1;
        RETURN NEW;
    END
    $$;

-- A simple single-event, row-level trigger executing a user-defined function.
CREATE TRIGGER bump_version
    BEFORE UPDATE ON film
    FOR EACH ROW
    EXECUTE FUNCTION bump_version();

-- A multi-event trigger executing a built-in function with literal string arguments; this is
-- the case that needs no user function and exercises OR'd events plus function arguments.
CREATE TRIGGER film_fulltext_trigger
    BEFORE INSERT OR UPDATE ON film
    FOR EACH ROW
    EXECUTE FUNCTION tsvector_update_trigger('fulltext', 'pg_catalog.english', 'title', 'description');
