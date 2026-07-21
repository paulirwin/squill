-- A plpgsql stored procedure. The body is stored verbatim, so it round-trips exactly as
-- written here. Procedures are deployed after tables, so a body may reference any table
-- declared in the project.
CREATE PROCEDURE add_book(author_name varchar(200), book_title varchar(400))
LANGUAGE plpgsql
AS $$
DECLARE
    existing_author_id integer;
BEGIN
    SELECT author_id INTO existing_author_id
    FROM author
    WHERE name = author_name;

    IF existing_author_id IS NULL THEN
        INSERT INTO author (name) VALUES (author_name)
        RETURNING author_id INTO existing_author_id;
    END IF;

    INSERT INTO book (author_id, title) VALUES (existing_author_id, book_title);
END;
$$;

-- An overload: same name, different argument types. PostgreSQL treats these as distinct
-- objects, and so does Squill — the argument signature is part of a procedure's identity.
CREATE PROCEDURE add_book(existing_author_id integer, book_title varchar(400))
LANGUAGE plpgsql
AS $$
BEGIN
    INSERT INTO book (author_id, title) VALUES (existing_author_id, book_title);
END;
$$;
