CREATE TABLE reservation
(
    a integer,
    b integer,
    c integer,
    UNIQUE (a, b) INCLUDE (c)
);
