-- btree_gist is what lets a scalar column (room) sit alongside a range column in one
-- gist-backed exclusion constraint. Without it the server rejects the constraint outright.
CREATE EXTENSION btree_gist;

CREATE TABLE booking
(
    booking_id integer PRIMARY KEY,
    room       integer   NOT NULL,
    during     tstzrange NOT NULL,
    CONSTRAINT no_overlap EXCLUDE USING gist (room WITH =, during WITH &&)
);
