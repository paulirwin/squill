-- Every facet at once: an explicit name, a WHERE predicate, INCLUDE columns, storage
-- parameters and a DEFERRABLE spec.
CREATE TABLE shift
(
    shift_id  integer PRIMARY KEY,
    staff_id  integer NOT NULL,
    slot      integer NOT NULL,
    note      text,
    cancelled boolean NOT NULL DEFAULT false,
    CONSTRAINT no_double_booking EXCLUDE (staff_id WITH =, slot WITH =)
        INCLUDE (note)
        WITH (fillfactor = 70)
        WHERE (cancelled = false)
        DEFERRABLE INITIALLY DEFERRED
);
