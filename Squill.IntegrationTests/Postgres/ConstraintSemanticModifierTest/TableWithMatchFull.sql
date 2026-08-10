CREATE TABLE tenant
(
    tenant_id integer,
    region_id integer,
    PRIMARY KEY (tenant_id, region_id)
);

CREATE TABLE assignment
(
    id        integer PRIMARY KEY,
    tenant_id integer,
    region_id integer,
    CONSTRAINT fk_assignment_tenant FOREIGN KEY (tenant_id, region_id)
        REFERENCES tenant (tenant_id, region_id) MATCH FULL ON DELETE CASCADE
);
