-- Stamps actor.last_update on every UPDATE via the shared last_updated() trigger function
-- (see Programmability/LastUpdated.sql). One such trigger exists per table that carries a
-- last_update column.
CREATE TRIGGER last_updated
    BEFORE UPDATE ON actor
    FOR EACH ROW
    EXECUTE FUNCTION last_updated();
