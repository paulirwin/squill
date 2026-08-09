CREATE TABLE measurement
(
    id       integer PRIMARY KEY,
    reading  integer,
    quality  integer,
    CONSTRAINT ck_measurement_reading CHECK (reading > 0) NO INHERIT,
    CONSTRAINT ck_measurement_quality CHECK (quality >= 0)
);
