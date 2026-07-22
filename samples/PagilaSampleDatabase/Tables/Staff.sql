-- Store employees. staff.store_id references store, and store.manager_staff_id references
-- staff back the other way (see Store.sql) — the two tables form a circular FK pair that
-- Squill deploys by creating both tables before resolving the references between them. The
-- picture column stores a small image inline as bytea.
CREATE TABLE staff
(
    staff_id    integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    first_name  varchar(45) NOT NULL,
    last_name   varchar(45) NOT NULL,
    address_id  integer NOT NULL REFERENCES address (address_id),
    email       varchar(50),
    store_id    integer NOT NULL REFERENCES store (store_id),
    active      boolean NOT NULL DEFAULT true,
    username    varchar(16) NOT NULL,
    password    varchar(40),
    last_update timestamp NOT NULL DEFAULT now(),
    picture     bytea
);
