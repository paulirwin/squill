-- Stamps address.last_update on every UPDATE via the shared last_updated() trigger function
-- (see Programmability/LastUpdated.sql).
CREATE TRIGGER last_updated
    BEFORE UPDATE ON address
    FOR EACH ROW
    EXECUTE FUNCTION last_updated();
