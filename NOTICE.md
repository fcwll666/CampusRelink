# Notices and provenance

CampusRelink is independently developed and maintained for campus network authentication and reconnection, with specific support for Nanjing University of Chinese Medicine.

CampusRelink is distributed under the MIT license in LICENSE. The name and default school endpoint do not imply endorsement by the university or a network operator.

The CampusRelink branding in `branding/CampusRelink` is newly created artwork, provided with its build script under the repository's MIT license. The executable embeds the supplied multi-size `CampusRelink.ico` as its Windows icon and as the `CampusRelink.AppIcon` manifest resource; the application does not need external artwork files at runtime. No original bundled icon or other artwork from an earlier CampusFlow installation is distributed, and no claim is made about redistribution rights for those earlier assets. The branding uses system fonts for wordmarks and previews; no font files are redistributed. The CampusRelink name has not been checked for trademark or repository-name availability.

The build uses Windows and .NET Framework APIs. Those platforms and tools remain subject to their own terms and are not included in the release packages. GitHub Actions referenced by the workflow execute from their respective upstream projects and are not vendored into this repository.

The project defaults to Nanjing University of Chinese Medicine and the editable endpoint `http://net.njucm.edu.cn/api/portal/v1/login`. This is a built-in default, not a statement that a live campus test has been performed. Source and binary packages contain no real campus credentials, personal configuration or developer-host Wi-Fi lists. Each user supplies credentials locally and can change the login URL. The default HTTP endpoint transmits authentication without transport encryption; HTTPS login URLs retain normal certificate verification.

The CampusRelink rename does not migrate internal application identities. Data remains under `CampusFlowCommunity`, with the existing Run, task and mutex identifiers and the `CAMPUSFLOW_COMMUNITY_DATA_DIR` test override retained for compatibility with earlier community versions.
