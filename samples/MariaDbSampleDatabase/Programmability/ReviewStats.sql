-- A read-only procedure with an OUT parameter, declaring the characteristics that differ
-- from the engine defaults. Both MariaDB and MySQL default a routine to NOT DETERMINISTIC,
-- CONTAINS SQL and SQL SECURITY DEFINER, so only the non-default facets are written here —
-- and only those are recorded in the model.
CREATE PROCEDURE review_stats(IN target_book_id int, OUT average_rating decimal(3, 2))
READS SQL DATA
SQL SECURITY INVOKER
BEGIN
    SELECT AVG(rating) INTO average_rating
    FROM review
    WHERE book_id = target_book_id;
END;
