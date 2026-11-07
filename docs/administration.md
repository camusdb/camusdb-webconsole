# Administration

**Administration** in the header opens the node views. Each view shows data for one node only: the
node behind the console's endpoint.

| Tab | Who can see it, with authentication on |
| --- | --- |
| **Node** | A superuser, or a user with the `MONITOR` privilege |
| **Engine stats** | A superuser |
| **Statements** | A superuser |
| **Index usage** | A superuser |
| **Variables** | A superuser |
| **Backups** | A superuser |

To give a user the `MONITOR` privilege:

```sql
GRANT MONITOR ON *.* TO user;
```

The console checks the privilege when it connects. After a grant, connect again.

## Node

The **Node** tab shows the disk, CPU and memory of the node. It uses the server's node stats call:
`GET /v1/admin/stats` over REST, or `GetNodeStats` over gRPC.

| Area | Shows |
| --- | --- |
| **Disks** | Each volume that the engine writes to: the used part, the free space, the engine's own files, and the write watermark (`min_free_disk_bytes`) |
| **CPU** | The server process's part of all processors over the last minute, the processor count, and the host load averages |
| **Memory** | The host memory in use (the cgroup limit, if there is one), the process working set, and the managed heap |

- *Below watermark* means that the node refuses writes with `CADB0536`.
- *Low* means less than 4 times the watermark.
- A disk bar turns orange at 90% used.
- *No limit* means that the heap has no limit.
- If the node could not read a value, the tab shows the value and the reason.
- **Take reading** reads the values again. **Auto every 5s** reads them each 5 seconds.
- With more than one endpoint, the **Node** list selects the node to read.
