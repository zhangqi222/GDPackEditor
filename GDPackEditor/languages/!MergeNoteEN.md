
# MOD Merge Tool User Guide

This tool supports merging arc and arz packages. Choose two packages with the same extension in the Package 1 / Package 2 fields, then click Scan to begin merging.


## Merging the basics: just handle the【Conflict】files
- 【Conflict】files are files with the same name present in both packages. You must pick one of the two, compare-edit then use the new file, or rename them so they can coexist.
- This tool provides compare-edit, rename, and choose-package-1-or-package-2 decision support.

## Decisions
- 【Use 1】: Use the Package 1 file; the same-named Package 2 file is discarded and does not enter the merge.
- 【Use 2】: Use the Package 2 file; the same-named Package 1 file is discarded and does not enter the merge.
- 【Rename】: Modify the Package 1 / Package 2 file so the names differ and they can coexist (e.g. when renaming tex and other material names, you must also update the corresponding reference paths in the arz).
- 【Compare Edit】: Content-level editing; shows the content of the same-named Package 1 / Package 2 file side by side, and you decide the final adopted value.
- 【Compare Edit】: For dbr, a template editor is provided. Package 1 / Package 2 values are shown read-only; the Final Adopted Value is the value used at pack time. By default, values unique to either file are adopted, and conflicting values adopt 2 (consistent with the default logic of merging Package 2 into Package 1).
- 【Compare Edit】: For txt / lua / tpl, a text editor is provided, with built-in simple syntax coloring and text diff.
- 【Compare Edit】: Other file types cannot be edited.
- 【合并 / Merge】: Use the file after compare-edit. After compare-editing, it is selectable in the decision; that is, if you decide the edited file is not good, you can switch back to Use 1, Use 2, etc.
- 【改 / Rename】: Use the file after renaming. After renaming, it is selectable in the decision; that is, if you decide renaming is unnecessary and Package 1 is fine, you can select Package 1 directly.
- 【Checkbox】: When checked it participates in the merge; when unchecked it does not. This has the highest priority. Even if you have set a Rename / Edit / Use 1 / Use 2 decision, an unchecked file will not enter the merged package.
- Files unique to Package 1 or Package 2 have no editing features. If you need to edit them, merge first and then drop them into the ARZ/ARC editor.

## Batch operations
- Batch operations are provided: Select All, Deselect All, Expand, Collapse, Use All 1, Use All 2, Use All Merge (files that were compare-edited), Use All Rename (files renamed to coexist).
- 【Select All / Deselect All】: Checked files participate in the final merge; unchecked files do not.
- 【Expand / Collapse】: Expand all folders, collapse all folders.
- 【Use All 1 / Use All 2 / Use All Merge / Use All Rename】: As the name implies, batch-apply the used file at the decision buttons (only affects the【checked rows】of the【currently displayed result】; it does not affect content that is not displayed).


## Filter:
- Conflict: shows files with the same name but different content (the core files you need to handle when merging a MOD).
- Same: shows files with the same name and the same content (no choice is provided, because since the data is identical, using Package 1 or Package 2 is the same).
- Renamed: shows files that have been renamed.
- Edited: shows files that have been compare-edited.
- Package 1: all files in Package 1.
- Package 2: all files in Package 2.
- Search: shows files matching the search content; you can search folders, file names, descriptions, and localization content (i.e. skill names, monster names, item names, etc. — select a language pack in the【Localize Files】dialog first).

## dbr compare editor
- Displays the Package 1 value and Package 2 value; these cannot be modified and are for reference only.
- Provides an Adopt column for quickly deciding which value to use.
- The Final Adopted Value is the value used at pack time. By default the values unique to the two files are adopted, and conflicting values adopt the Package 2 value by default.


## Limitations
- After renaming you cannot edit; you can undo the rename and then edit.
- After editing you cannot rename; you can undo the edit and then rename.

## Merge
- Before starting the merge, be sure to click the【All】filter button and check the file checkbox state.

## Other
- This tool never modifies the original data of Package 1 / Package 2; edits are made only in a temporary directory. The merged new package gets a `_merged` suffix by default, which you can change yourself.

## FAQ

1. Why are files missing from the merged package?
- Check the checkbox state when packing — maybe you forgot to check some files. Click【All】in the filter, then click【Select All】.

2. Why do many folders / files have no localization?
- Folder localization has only built-in Chinese and is incomplete; however you can export the dictionary, fill in the gaps yourself, and import it.
- File localization is implemented through language packs such as text_zh.arc, and only supports dbr files that contain tag fields.
