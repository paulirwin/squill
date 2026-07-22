-- Keeps film.fulltext (a tsvector) in sync with the film's title and description. Uses the
-- built-in tsvector_update_trigger function, whose arguments name the target tsvector column,
-- the text-search configuration ('pg_catalog.english'), and the source columns to index.
-- Fires BEFORE INSERT OR UPDATE so the search vector is always current before the row is
-- written. Paired with the GiST index film_fulltext_idx (see Tables/Film.sql).
CREATE TRIGGER film_fulltext_trigger
    BEFORE INSERT OR UPDATE ON film
    FOR EACH ROW
    EXECUTE FUNCTION tsvector_update_trigger('fulltext', 'pg_catalog.english', 'title', 'description');
