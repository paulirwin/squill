-- Round-trip fixture for the CREATE TRIGGER declaration modifiers (issue #214): WHEN,
-- UPDATE OF, REFERENCING transition tables, and CREATE CONSTRAINT TRIGGER. Each of these
-- used to throw NotImplementedException, so a schema declaring one could not be built at all.
--
-- Every modifier changes how often, or whether, the body runs, which is why the test asserts
-- firing behaviour and not merely that the DDL was accepted.

-- Records one row per trigger firing, so a test can count how many times each fired.
-- Declared before film so the source order matches the alphabetical order the database
-- extractor reports tables in; the model hash is order-sensitive.
CREATE TABLE audit_log (
    id integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    source text NOT NULL
);

CREATE TABLE film (
    film_id integer PRIMARY KEY,
    title text NOT NULL,
    rating text,
    version integer NOT NULL
);

CREATE FUNCTION record_firing() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
    BEGIN
        INSERT INTO audit_log (source) VALUES (TG_ARGV[0]);
        RETURN NULL;
    END
    $$;

-- WHEN: fires only when the title actually changed. IS DISTINCT FROM is the idiom here
-- because either side may be null.
CREATE TRIGGER title_changed
    AFTER UPDATE ON film
    FOR EACH ROW
    WHEN (OLD.title IS DISTINCT FROM NEW.title)
    EXECUTE FUNCTION record_firing('title_changed');

-- UPDATE OF: fires only for an update that names the rating column, whatever its value.
CREATE TRIGGER rating_touched
    AFTER UPDATE OF rating ON film
    FOR EACH ROW
    EXECUTE FUNCTION record_firing('rating_touched');

-- REFERENCING: a statement-level trigger reading the transition tables by name.
CREATE TRIGGER statement_audit
    AFTER UPDATE ON film
    REFERENCING OLD TABLE AS before_rows NEW TABLE AS after_rows
    FOR EACH STATEMENT
    EXECUTE FUNCTION record_firing('statement_audit');

-- CONSTRAINT TRIGGER: deferred to commit time rather than firing on the statement.
CREATE CONSTRAINT TRIGGER deferred_audit
    AFTER INSERT ON film
    DEFERRABLE INITIALLY DEFERRED
    FOR EACH ROW
    EXECUTE FUNCTION record_firing('deferred_audit');
