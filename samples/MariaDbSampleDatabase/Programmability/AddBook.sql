-- A stored procedure. The body is stored verbatim, so it round-trips exactly as written
-- here. Procedures are deployed after tables, so a body may reference any table declared
-- in the project.
--
-- Unlike PostgreSQL, MariaDB and MySQL do not allow overloading: a name identifies at most
-- one procedure in a database, so there is no argument signature in its identity. There is
-- also no LANGUAGE clause — SQL is the only routine language either engine supports.
CREATE PROCEDURE add_book(author_name varchar(200), book_title varchar(400))
MODIFIES SQL DATA
BEGIN
    DECLARE existing_author_id int;

    SELECT author_id INTO existing_author_id
    FROM author
    WHERE name = author_name
    LIMIT 1;

    IF existing_author_id IS NULL THEN
        INSERT INTO author (name) VALUES (author_name);
        SET existing_author_id = LAST_INSERT_ID();
    END IF;

    INSERT INTO book (author_id, title) VALUES (existing_author_id, book_title);
END;
