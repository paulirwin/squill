-- The Sakila `store` table. Together with `staff` it forms a circular foreign-key dependency:
-- `store.manager_staff_id` references `staff`, while `staff.store_id` references `store`.
-- Sakila keeps both sides declarative with named FK constraints; a deployment tool must order
-- creation so that the constraints can be satisfied (typically by deferring or adding one FK
-- after both tables exist).
--
-- `idx_unique_manager` is a UNIQUE index enforcing that a staff member manages at most one
-- store.
CREATE TABLE store
(
    store_id         int unsigned NOT NULL AUTO_INCREMENT PRIMARY KEY,
    manager_staff_id int unsigned NOT NULL,
    address_id       int unsigned NOT NULL,
    last_update      timestamp NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
    CONSTRAINT fk_store_staff FOREIGN KEY (manager_staff_id) REFERENCES staff (staff_id),
    CONSTRAINT fk_store_address FOREIGN KEY (address_id) REFERENCES address (address_id)
);

CREATE UNIQUE INDEX idx_unique_manager ON store (manager_staff_id);
CREATE INDEX idx_fk_address_id ON store (address_id);
