-- Stamps film.last_update on every UPDATE via the shared last_updated() trigger function
-- (see Programmability/LastUpdated.sql). Independent of the film_fulltext_trigger, which
-- maintains the search vector on the same table.
CREATE TRIGGER last_updated
    BEFORE UPDATE ON film
    FOR EACH ROW
    EXECUTE FUNCTION last_updated();
