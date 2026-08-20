-- No USING clause, so the access method defaults to btree, and no constraint name, so the
-- server derives one. Both are what the model must predict for this to round-trip.
CREATE TABLE seat
(
    seat_id integer PRIMARY KEY,
    section integer NOT NULL,
    row_no  integer NOT NULL,
    EXCLUDE (section WITH =, row_no WITH =)
);
