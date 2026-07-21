-- Two tables that reference each other. No create order can satisfy both foreign keys, so
-- the one that closes the cycle is deferred to an ALTER TABLE after both tables exist.
CREATE TABLE husband
(
    id      integer PRIMARY KEY,
    wife_id integer NULL REFERENCES wife (id)
);

CREATE TABLE wife
(
    id         integer PRIMARY KEY,
    husband_id integer NULL REFERENCES husband (id)
);
