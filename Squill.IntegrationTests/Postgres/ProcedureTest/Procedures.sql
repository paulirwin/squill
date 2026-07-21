CREATE TABLE widgets
(
    id   integer PRIMARY KEY,
    name varchar(100) NOT NULL
);

-- A plpgsql procedure that writes to a table declared in the same project, which only
-- deploys cleanly if procedures are created after the tables they use.
CREATE PROCEDURE add_widget(widget_id integer, widget_name varchar(100))
LANGUAGE plpgsql
AS $$
BEGIN
    INSERT INTO widgets (id, name) VALUES (widget_id, widget_name);
END;
$$;

-- A SQL-language procedure, to cover a language other than plpgsql.
CREATE PROCEDURE clear_widgets()
LANGUAGE sql
AS $$
DELETE FROM widgets;
$$;

-- An overload of add_widget: same name, different argument types, so it must round-trip
-- as a distinct object rather than colliding with the procedure above.
CREATE PROCEDURE add_widget(widget_name text)
LANGUAGE plpgsql
SECURITY DEFINER
AS $$
BEGIN
    INSERT INTO widgets (id, name) VALUES (0, widget_name);
END;
$$;
