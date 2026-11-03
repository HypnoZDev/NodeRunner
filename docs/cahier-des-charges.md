# NodeRunner Studio : cahier des charges

Pourquoi ce logiciel, pour qui, et ce qu'il doit savoir faire. J'ai écrit ce document en partant de ce qui m'agace
(et de ce qui agace la plupart des mappeurs, d'après les forums Cfx.re et les discussions FiveM) quand on travaille sur
la carte de GTA V avec CodeWalker, OpenIV ou Sollumz. Ces outils sont excellents et NodeRunner ne serait rien sans
CodeWalker ; l'idée n'est pas de les remplacer pour tout, mais de rendre plus simple le travail de tous les jours d'un
mappeur FiveM.

Il s'adresse surtout à trois profils :
- **les mappeurs** (FiveM ou solo) qui ajoutent, déplacent ou retirent des objets de la carte ;
- **les développeurs FiveM** qui ont besoin de positions, de noms de modèles et de hash pour leurs scripts ;
- **les curieux / dataminers** qui veulent comprendre comment la carte est construite.

---

## 1. Ce que je veux régler

### 1. Les ralentissements sur les gros projets
Avec beaucoup d'objets ou de gros YMAP ouverts, CodeWalker devient lent, et il lui arrive de planter (d'où le réflexe
d'enregistrer toutes les deux minutes).
**Mon approche :** un moteur DirectX 11 sur .NET 10, chargement en arrière-plan autour de la caméra, niveaux de détail
(LOD) gérés comme le jeu. Sur mon PC (RX 6750 XT), la carte entière tourne autour de 180 FPS ; ce n'est pas une
garantie, ça dépendra de chaque machine, mais c'est l'objectif de rester fluide.

### 2. Les versions récentes du jeu (GTA V Enhanced)
Les outils existants demandent souvent des manipulations pour lire les fichiers de la version Enhanced.
**Mon approche :** détecter toute seule l'installation (Legacy ou Enhanced) et la lire sans manipulation.
Pour l'instant, NodeRunner reconnaît les deux versions mais il est développé et testé sur Legacy ; Enhanced reste à
vérifier de bout en bout.

### 3. Les objets qui flottent ou s'enfoncent
Poser un objet bien au sol, c'est souvent ajuster le Z à la main, au centimètre, en tournant la caméra dans tous les sens.
**Mon approche :** des poignées 3D avec crans (Ctrl), et surtout une touche pour **poser l'objet sur la collision** du sol
sous lui, plus une option pour qu'il suive le sol pendant qu'on le déplace.

### 4. Les suppressions qui laissent des traces
On supprime un bâtiment, et en jeu il reste un mur invisible (sa collision), une version floue au loin (un LOD), ou un
trou dans le décor (les boîtes d'occlusion). Et deux ressources qui modifient le même YMAP se marchent dessus sans
prévenir : la dernière chargée gagne.
**Mon approche :** une suppression qui emporte toute la chaîne de LOD, retire la collision et l'occlusion de l'objet à
l'export, avec un aperçu avant de valider, et une vérification avant export qui signale les YMAP déjà livrés par une
autre ressource du serveur. Plus tard : l'occlusion audio (`.dat151`), pour que le son d'un bâtiment disparu ne reste pas.

### 5. L'export FiveM fastidieux
Exporter en XML, reconvertir, créer le `fxmanifest.lua` à la main, ranger le dossier `stream/`, penser à recalculer les
extents... C'est là qu'on perd le plus de temps, et là que naissent la plupart des « ça marche dans CodeWalker mais pas
en jeu ».
**Mon approche :** un bouton « Exporter en ressource FiveM » qui fait tout : manifeste, dossier `stream`, boîtes du YMAP
recalculées à chaque écriture.

---

## 2. Fonctions attendues

Les cases cochées sont faites ; certaines attendent encore un test en jeu.

### A. Manipulation 3D (mappeurs)
- [x] **Déplacer, tourner, mettre à l'échelle** avec des poignées 3D, ou en tapant les valeurs.
- [x] **Crans** (Ctrl) : 0,5 m, 15°, 0,1 d'échelle.
- [x] **Annuler / rétablir** sans limite (`Ctrl+Z` / `Ctrl+Y`).
- [x] **Sélection multiple** (Ctrl+clic) et **copier / coller** (`Ctrl+C` / `Ctrl+V`) : j'ai préféré ça à un `Ctrl+D`,
  c'est plus naturel pour tout le monde.
- [x] **Poser au sol** sur la collision (touche Fin), et « suivre le sol » pendant un déplacement.
- [ ] **Accroche aux sommets** d'un autre objet.

### B. YMAP et export (mappeurs et devs)
- [x] **Boîtes du YMAP recalculées automatiquement** à chaque enregistrement, pour éviter les objets qui clignotent ou
  disparaissent.
- [x] **Suppression propre des objets d'origine** : chaîne de LOD, collision (`.ybn`) et occluders retirés, avec aperçu.
- [x] **Export FiveM en un clic** : dossier de ressource avec `fxmanifest.lua` et `stream/`.
- [x] **Vérification avant export** : conflits avec les autres ressources du serveur et avec mes autres projets.
- [ ] **Fusion** de deux ressources qui modifient le même YMAP.
- [ ] **Import de modèles Sollumz** avec génération du `.ytyp` et du `_manifest.ymf`.

### C. Intérieurs (MLO) et audio
- [x] **Salles et portails** affichés en 3D, avec le nom de la salle au survol, pour vérifier ce qui sépare l'intérieur
  de l'extérieur.
- [x] **Entity sets** : afficher / masquer en direct les variantes de mobilier des intérieurs d'origine (bunkers,
  appartements, clubs...) et copier les lignes de script qui les activent.
- [ ] **Occlusion audio** : voir (puis éditer) les zones d'écho et de réverbération liées aux bâtiments (`.dat151` / `.dat54`).

### D. Datamining et scripts (devs)
- [x] **Hash ↔ nom** : `Ctrl+K` accepte un hash (`0x...` ou décimal) et retrouve le type correspondant. Les noms
  viennent des fichiers du jeu installé ; on peut compléter avec sa propre liste dans `data/strings.txt` (je ne
  distribue aucune liste de noms du jeu).
- [x] **Copie en un clic** d'une position ou d'une sélection :
  - `vector3(...)` / `vector4(...)` (Lua, FiveM) ;
  - objet JSON ;
  - C# (`new Vector3(...)`).
- [x] **Recherche rapide `Ctrl+K`** : nom d'objet, hash, YMAP, type, ou coordonnées collées.
- Calques :
  - [x] décor (YMAP), navmesh, pathnodes, points de scénario ;
  - [x] collisions (`.ybn`) ;
  - [ ] lumières et boîtes d'occlusion.

---

## 3. Ce que NodeRunner ne fera pas

- **Rien pour GTA Online**, aucun contournement d'anti-triche : solo et FiveM uniquement.
- **Aucun fichier du jeu fourni** : le logiciel lit uniquement l'installation de GTA V de l'utilisateur.
- **Pas d'accès payant** : le logiciel est et restera gratuit, sans version « premium » ni accès anticipé pour les
  donateurs. Le code source est consultable sur GitHub, mais ce n'est pas un projet open source au sens strict :
  la licence (à venir) interdira la revente et la redistribution sous un autre nom.

---

## 4. Ordre de priorité

1. Fait : poser au sol, boîtes du YMAP recalculées, export FiveM.
2. Fait : hash, copie multi-formats, entity sets, salles et portails.
3. En cours : vérification avant export et conflits (la détection est faite, la fusion reste).
4. Ensuite : import Sollumz et `.ytyp`, puis pack solo (`dlc.rpf`).
5. Plus tard : calques lumières et occlusion, occlusion audio, comparateur visuel de YMAP.
